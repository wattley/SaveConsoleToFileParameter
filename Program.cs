using System;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using static System.Net.Mime.MediaTypeNames;
using System.Text;

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

    static UdpClient udpListener;
    static int UDP_PORT = 9000;

    static void checkExitKey()
    {
        if (Console.KeyAvailable)
        {
            ConsoleKey key = Console.ReadKey(intercept: true).Key;
            keepRunning = (key != ConsoleKey.Q);
        }
    }

    static void MakeLogFileIfNotExists()
    {
        today = DateTime.Now;
        filePath = fileSpec.Replace("[DATETIME]", today.ToString(wrapDaily ? FILE_DATE_FORMAT : FILE_DATE_TIME_FORMAT));

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

    static void Main(string[] args)
    {
        //string filePath = @"C:\Users\Jan\LightCat Dropbox\Projects\Trevor\VoidDetect\Calculations and Data\20241223 Console Output.txt";
        //string portName = "COM5"; // Replace with your port name
        //int baudRate = 115200; // Replace with your baud rate
        bool HAVE_SERIAL_PORT = false, HAVE_FILE_PATH = false, HAVE_BAUD_RATE = false, HAVE_UDP_PORT = false;
        SerialPort serialPort = null;
        string previousMessage = "";

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
                Int32.TryParse(args[i + 1], out UDP_PORT);
                HAVE_UDP_PORT = true;
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

        bool configured_correctly = HAVE_FILE_PATH && HAVE_BAUD_RATE && (HAVE_SERIAL_PORT || HAVE_UDP_PORT);
        if (wrapDaily && !fileSpec.Contains("[DATETIME]"))
        {
            Console.WriteLine("ERROR: Wrap daily option requires [DATETIME] in the file path.\n");
            configured_correctly = false;
        }

        if (!configured_correctly)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine($"\t-f <LOG FILE PATH, put [DATETIME] in filename for it to be replaced by {FILE_DATE_TIME_FORMAT}>");
            Console.WriteLine("\t-p <COM PORT> or \t-udp <UDP PORT>");
            Console.WriteLine("\t-b <BAUD RATE>");
            Console.WriteLine($"\t-d\tWrap log file daily, [DATETIME] in filename replaced by {FILE_DATE_FORMAT}>");
            return;
        }

        MakeLogFileIfNotExists();

        while (keepRunning)
        {
            if (HAVE_SERIAL_PORT)
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
                    catch /*(Exception ex)*/
                    {
                        // Console.WriteLine($"Error: {ex.Message}");
                        serialPort.Dispose();
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

            if (HAVE_UDP_PORT)
            {
                udpListener = new UdpClient(UDP_PORT);
                udpListener.Client.Blocking = false;
                Console.WriteLine($"Started UDP listener on port {UDP_PORT}");
            }


            Boolean write_time = false;
            String text = "", received_text = "";

            // Continuously read data
            while (keepRunning)
            {
                checkExitKey();

                DateTime now = DateTime.Now;
                bool messageReceived = false;

                if (HAVE_SERIAL_PORT)
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
                        break;
                    }
                }

                if (HAVE_UDP_PORT)
                {
                    try
                    {
                        if (udpListener.Available > 0)
                        {
                            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                            byte[] buffer = udpListener.Receive(ref remoteEP);
                            string receivedText = Encoding.UTF8.GetString(buffer);

                            received_text += receivedText;
                        }
                    }
                    catch (SocketException ex)
                    {
                        // Ignore socket exceptions from non-blocking operation
                        Console.WriteLine($"UDP Error: {ex.Message}");
                    }
                }

                while (received_text.Length > 0 && !messageReceived)
                {
                    char c = received_text[0];
                    received_text = received_text.Substring(1);

                    if (write_time && (c != '\r') && (c != '\n'))
                    {
                        write_time = false;
                        String now_text = now.ToString(LOG_DATE_TIME_FORMAT) + ": ";
                        Console.Write(now_text);
                        text += now_text;
                    }

                    // Display the data on the console
                    Console.Write(c);

                    // Write the data to the file
                    text += c;

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
                            writer.Write(text);
                            writer.Flush();
                            text = "";
                        }
                        write_time = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                        Console.WriteLine(" --- will try again later");
                    }
                }


            }   // while (keepRunning)

            if (serialPort != null && serialPort.IsOpen)
            {
                Console.WriteLine($"\nClosing {portName} ");
                serialPort.Close();
            }
            serialPort = null;

            // Add cleanup for UDP at the end of Main:
            if (udpListener != null)
            {
                udpListener.Close();
                udpListener = null;
            }
        }   // while (keepRunning)
    }
}
