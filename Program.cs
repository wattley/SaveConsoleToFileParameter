using System;
using System.Data.SQLite; // Install "System.Data.SQLite" via NuGet
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using static System.Net.Mime.MediaTypeNames;
using System.Text;
using System.Collections.Generic;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleTitle(string lpConsoleTitle);

    static bool keepRunning = true;
    const String FILE_DATE_TIME_FORMAT = "yyMMddHHmm";
    const String FILE_DATE_FORMAT = "yyMMdd";
    const String LOG_DATE_TIME_FORMAT = "yyyy-MM-dd HH:mm:ss";
    static string fileSpec = "", filePath = "";
    static string portName = "";
    static int baudRate = 0;
    static bool wrapDaily = false;
    static DateTime today;

    static UdpClient udpLogListener = null;
    static List<UdpClient> udpTelemetryListeners = new List<UdpClient>();
    static int UDP_LOG_PORT = 9000;
    static List<Int32> udp_telemetry_ports = new List<Int32>();

    static Boolean write_time = false;
    static String log_text = "", received_text = "";

    // ----------------------------------------------------------------
    // Check for the exit key (Q) being pressed
    // ----------------------------------------------------------------
    static void checkExitKey()
    {
        if (Console.KeyAvailable)
        {
            ConsoleKey key = Console.ReadKey(intercept: true).Key;
            keepRunning = (key != ConsoleKey.Q);
        }
    }

    // ----------------------------------------------------------------
    // Create the log file if it doesn't exist, and write a header line
    // ----------------------------------------------------------------
    static void MakeLogFileIfNotExists()
    {
        filePath = fileSpec.Replace("[DATETIME]", today.ToString(wrapDaily ? FILE_DATE_FORMAT : FILE_DATE_TIME_FORMAT)) + ".log";

        string title = portName + "->" + filePath;
        SetConsoleTitle(title);

        Boolean fileExists = File.Exists(filePath);

        // Ensure the file is created if it doesn't exist
        using (FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            if (fileExists)
            {
                Console.WriteLine($"Appending to file [{filePath}]");
            }
            else
            {
                Console.WriteLine($"Created file [{filePath}]");
            }
            using (StreamWriter writer = new StreamWriter(fs))
            {
                writer.Write(DateTime.Now.ToString(LOG_DATE_TIME_FORMAT));
                writer.Write(": LOG STARTED\n");
                writer.Flush(); // Ensure data is written to the file immediately
            }
        }
    }

    // ----------------------------------------------------------------
    // Create the SQLite database if it doesn't exist
    // ----------------------------------------------------------------
    static SQLiteConnection sqliteConnection = null;

    static void MakeSQLiteDatabase()
    {
        string dbPath = fileSpec.Replace("[DATETIME]", today.ToString(wrapDaily ? FILE_DATE_FORMAT : FILE_DATE_TIME_FORMAT)) + ".db";
        bool createNewDatabase = !File.Exists(dbPath);
        if (createNewDatabase)
        {
            SQLiteConnection.CreateFile(dbPath);
        }

        sqliteConnection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
        sqliteConnection.Open();

        using (var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", sqliteConnection))
            cmd.ExecuteNonQuery();

        if (createNewDatabase)
        {
            using (var cmd = new SQLiteCommand("CREATE TABLE packets (id INTEGER PRIMARY KEY, dt INTEGER, src INTEGER, port INTEGER, data BLOB);", sqliteConnection))
                cmd.ExecuteNonQuery();
        }
    }

    // ----------------------------------------------------------------
    // Check the command line arguments
    // ----------------------------------------------------------------
    //string filePath = @"C:\Users\Jan\LightCat Dropbox\Projects\Trevor\VoidDetect\Calculations and Data\20241223 Console Output.txt";
    //string portName = "COM5"; // Replace with your port name
    //int baudRate = 115200; // Replace with your baud rate
    static bool HAVE_SERIAL_PORT = false, HAVE_FILE_PATH = false, HAVE_BAUD_RATE = false, HAVE_UDP_PORT = false, HAVE_TELEMETRY_PORT = false;

    static bool CheckArguments(string[] args)
    {
        // Iterate through the arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-p" && i + 1 < args.Length)
            {
                portName = args[i + 1];
                HAVE_SERIAL_PORT = true;
            }
            else if (args[i] == "-udp" && i + 1 < args.Length)
            {
                Int32.TryParse(args[i + 1], out UDP_LOG_PORT);
                HAVE_UDP_PORT = true;
            }
            else if (args[i] == "-tlm" && i + 1 < args.Length)
            {
                HAVE_TELEMETRY_PORT = true;
                foreach (String port in args[i + 1].Split(','))
                {
                    if (Int32.TryParse(port, out int tlm_port))
                    {
                        udp_telemetry_ports.Add(tlm_port);
                    }
                    else
                    {
                        Console.WriteLine($"Error: Invalid telemetry port '{port}'");
                        HAVE_TELEMETRY_PORT = false;
                    }
                }
            }
            else if (args[i] == "-f" && i + 1 < args.Length)
            {
                fileSpec = args[i + 1];
                HAVE_FILE_PATH = true;
            }
            else if (args[i] == "-b" && i + 1 < args.Length)
            {
                Int32.TryParse(args[i + 1], out baudRate);
                HAVE_BAUD_RATE = true;
            }
            else if (args[i] == "-d")
            {
                wrapDaily = true;
            }
        }

        bool configured_correctly = HAVE_FILE_PATH && ((HAVE_BAUD_RATE && HAVE_SERIAL_PORT) || HAVE_UDP_PORT || HAVE_TELEMETRY_PORT);
        if (wrapDaily && !fileSpec.Contains("[DATETIME]"))
        {
            Console.WriteLine("ERROR: Wrap daily option requires [DATETIME] in the file path.\n");
            configured_correctly = false;
        }

        if (!configured_correctly)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine($"\t-f <LOG FILE PATH, put [DATETIME] in filename for it to be replaced by {FILE_DATE_TIME_FORMAT}>");
            Console.WriteLine("\t-p <COM PORT> or \t-udp <UDP PORT> or \t-tlm <UDP PORT>[,<UDP PORT>..]");
            Console.WriteLine("\t-b <BAUD RATE>");
            Console.WriteLine($"\t-d\tWrap log file daily, [DATETIME] in filename replaced by {FILE_DATE_FORMAT}>");
        }

        return configured_correctly;
    }

    // ----------------------------------------------------------------
    // Check that the serial port is connected
    // ----------------------------------------------------------------
    static SerialPort serialPort = null;
    static string previousMessage = "";

    static void CheckSerialPortConnected()
    {
        if (HAVE_SERIAL_PORT && HAVE_BAUD_RATE)
        {
            Console.Write($"Opening {portName} ");
            while (serialPort == null && keepRunning)
            {
                Console.Write("_");
                try
                {
                    // Open the serial port
                    serialPort = new SerialPort(portName, baudRate);
                    serialPort.Open();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                    if (serialPort != null)
                    {
                        serialPort.Dispose();
                    }
                    serialPort = null;
                    System.Threading.Thread.Sleep(500);
                }
                checkExitKey();
            }

            if (!keepRunning)
            {
                return;
            }
            Console.WriteLine(" READY!");
            Console.WriteLine($"Reading data from {portName}...");
            // serialPort.ReadTimeout = 500;

        }
    }

    // ----------------------------------------------------------------
    // Add any data received from the serial port to the received_text buffer
    // ----------------------------------------------------------------
    static bool CheckSerialPortData()
    {
        if (HAVE_SERIAL_PORT && HAVE_BAUD_RATE)
        {
            // Read all the data from the serial port
            try
            {
                if (serialPort.BytesToRead != 0)
                {
                    received_text += serialPort.ReadExisting();
                }
            }
            catch (TimeoutException)
            {
                // Ignore timeout exceptions
            }
            catch (Exception ex)
            {
                if (previousMessage != ex.Message)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                    previousMessage = ex.Message;
                }
            }
            if (!serialPort.IsOpen)
            {
                return false;
            }
        }
        return true;
    }

    // ----------------------------------------------------------------
    // Start the UDP log listener
    // ----------------------------------------------------------------
    static void StartLogListener()
    {
        if (HAVE_UDP_PORT && (udpLogListener == null))
        {
            udpLogListener = new UdpClient(UDP_LOG_PORT);
            udpLogListener.Client.Blocking = false;
            Console.WriteLine($"Started UDP listener on port {UDP_LOG_PORT}");
        }
    }

    // ----------------------------------------------------------------
    // Add any data received from the UDP log port to the received_text buffer
    // ----------------------------------------------------------------
    static void CheckUDPLogPortData()
    {
        if (HAVE_UDP_PORT)
        {
            try
            {
                if (udpLogListener.Available > 0)
                {
                    IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    byte[] buffer = udpLogListener.Receive(ref remoteEP);
                    string receivedText = Encoding.UTF8.GetString(buffer);

                    received_text += receivedText;
                    // Console.WriteLine($"Received {buffer.Length} bytes from {remoteEP.Address}:{remoteEP.Port}");
                }
            }
            catch (SocketException ex)
            {
                // Ignore socket exceptions from non-blocking operation
                Console.WriteLine($"UDP Error: {ex.Message}");
            }
        }
    }

    // ----------------------------------------------------------------
    // Start the UDP telemetry listeners
    // ----------------------------------------------------------------
    static void StartTelemetryListeners()
    {
        if (HAVE_TELEMETRY_PORT && (udpTelemetryListeners.Count == 0))
        {
            foreach (Int32 tlm_port in udp_telemetry_ports)
            {
                UdpClient tlm_listener = new UdpClient(tlm_port);
                tlm_listener.Client.Blocking = false;
                udpTelemetryListeners.Add(tlm_listener);
                Console.WriteLine($"Started UDP telemetry listener on port {tlm_port}");
            }

            MakeSQLiteDatabase();
        }
    }

    // ----------------------------------------------------------------
    // Check the UDP telemetry listeners
    // ----------------------------------------------------------------
    static SQLiteTransaction tx = null;
    const int batchSize = 100 * 1000;    // bytes required before writing to database
    const int writeDelayMs = 5000;  // milliseconds to wait after no longer receiving data before writing a batch to database
    static DateTime writeIsDue = DateTime.Now;
    static int dataReceived = 0;

    static void CheckUDPTelemetryData()
    {
        if (HAVE_TELEMETRY_PORT)
        {
            try
            {
                foreach (UdpClient tlm_listener in udpTelemetryListeners)
                {
                    if (tlm_listener.Available > 0)
                    {
                        if (tx == null)
                        {
                            tx = sqliteConnection.BeginTransaction();
                        }

                        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                        byte[] buffer = tlm_listener.Receive(ref remoteEP);
                        long now = DateTime.Now.Ticks;

                        // TABLE packets (id INTEGER PRIMARY KEY, dt INTEGER, src INTEGER, port INTEGER, data BLOB)
                        using (var cmd = new SQLiteCommand("INSERT INTO packets (dt, src, port, data) VALUES (@dt, @src, @port, @data);", sqliteConnection, tx))
                        {
                            var p_dt = cmd.Parameters.Add("@dt", System.Data.DbType.Int64);
                            p_dt.Value = now;
                            var p_src = cmd.Parameters.Add("@src", System.Data.DbType.Int64);
                            IPAddress sourceIp = remoteEP.Address;
                            p_src.Value = BitConverter.ToInt32(remoteEP.Address.GetAddressBytes(), 0);
                            var p_port = cmd.Parameters.Add("@port", System.Data.DbType.Int32);
                            p_port.Value = ((IPEndPoint)tlm_listener.Client.LocalEndPoint).Port;
                            var p_data = cmd.Parameters.Add("@data", System.Data.DbType.Binary);
                            p_data.Value = buffer;

                            cmd.ExecuteNonQuery();

                            dataReceived += buffer.Length;
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                // Ignore socket exceptions from non-blocking operation
                Console.WriteLine($"UDP Error: {ex.Message}");
            }
            if (dataReceived > 0)
            {
                if (dataReceived >= batchSize || DateTime.Now >= writeIsDue)
                {
                    tx.Commit();
                    tx.Dispose();
                    tx = null;
                    Console.WriteLine($"Wrote {dataReceived} bytes to database");
                    dataReceived = 0;
                    writeIsDue = DateTime.Now.AddMilliseconds(writeDelayMs);
                }
            }
        }
    }

    // ----------------------------------------------------------------
    // Write any completed log data received
    // ----------------------------------------------------------------
    static bool messageReceived = false;

    static void WriteLogFileData()
    {
        DateTime now = DateTime.Now;

        while (received_text.Length > 0 && !messageReceived)
        {
            char c = received_text[0];
            received_text = received_text.Substring(1);

            if (write_time && (c != '\r') && (c != '\n'))
            {
                write_time = false;
                String now_text = now.ToString(LOG_DATE_TIME_FORMAT) + ": ";
                Console.Write(now_text);
                log_text += now_text;
            }

            // Display the data on the console
            Console.Write(c);

            // Write the data to the file
            log_text += c;

            if ((c == '\n') || (c == '\r'))
            {
                messageReceived = true;
            }
        }

        // Write to log file if we have a complete message from either source
        if (messageReceived)
        {
            try
            {
                if (wrapDaily && (today.ToString(FILE_DATE_FORMAT) != now.ToString(FILE_DATE_FORMAT)))
                {
                    Console.WriteLine("--> Wrapping into a new log file for the day.");
                    MakeLogFileIfNotExists();
                }

                using (FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (StreamWriter writer = new StreamWriter(fs))
                {
                    writer.Write(log_text);
                    writer.Flush();
                    log_text = "";
                }
                write_time = true;
                messageReceived = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(" --- will try again later");
            }
        }
    }

    // =================================================================
    // MAIN
    // =================================================================
    static void Main(string[] args)
    {

        if (!CheckArguments(args))
        {
            return;
        }

        today = DateTime.Now;
        MakeLogFileIfNotExists();

        while (keepRunning)
        {
            today = DateTime.Now;
            CheckSerialPortConnected();
            StartLogListener();
            StartTelemetryListeners();

            // Continuously read data
            while (keepRunning)
            {
                checkExitKey();

                if (!CheckSerialPortData())
                {
                    break;
                }

                CheckUDPLogPortData();
                CheckUDPTelemetryData();

                WriteLogFileData();
            }   // while (keepRunning)

            // Cleanup for Serial Port (if it broke, or we are quitting)
            if (serialPort != null && serialPort.IsOpen)
            {
                Console.WriteLine($"\nClosing {portName} ");
                serialPort.Close();
            }
            serialPort = null;
        }   // while (keepRunning)

        // Cleanup for UDP log listener
        if (udpLogListener != null)
        {
            udpLogListener.Close();
        }

        // Cleanup for UDP Telemetry Listeners
        if (udpTelemetryListeners.Count > 0)
        {
            foreach (UdpClient tlm_listener in udpTelemetryListeners)
            {
                tlm_listener.Close();
            }
        }

        // Add cleanup for UDP at the end of Main:
        if (sqliteConnection != null)
        {
            if (tx != null)
            {
                tx.Commit();
                tx.Dispose();
            }
            sqliteConnection.Close();
        }
    }
}
