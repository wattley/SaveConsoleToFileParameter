using System;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using static System.Net.Mime.MediaTypeNames;

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

    static void checkEscapeKey()
    {
        if (Console.KeyAvailable)
        {
            ConsoleKey key = Console.ReadKey(intercept: true).Key;
            keepRunning = (key != ConsoleKey.Escape);
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
        int haveParameterFlags = 0;
        const int HAVE_PORT = 1, HAVE_FILE_PATH = 2, HAVE_BAUD_RATE = 4;
        SerialPort serialPort = null;
        string previousMessage = "";

        // Iterate through the arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-p" && i + 1 < args.Length)
            {
                portName = args[i + 1];
                haveParameterFlags |= HAVE_PORT;
            }
            else if (args[i] == "-f" && i + 1 < args.Length)
            {
                fileSpec = args[i + 1];
                haveParameterFlags |= HAVE_FILE_PATH;
            }
            else if (args[i] == "-b" && i + 1 < args.Length)
            {
                Int32.TryParse(args[i + 1], out baudRate);
                haveParameterFlags |= HAVE_BAUD_RATE;
            }
            else if (args[i] == "-d")
            {
                wrapDaily = true;
            }
        }

        bool misconfigured = haveParameterFlags != (HAVE_PORT + HAVE_FILE_PATH + HAVE_BAUD_RATE);
        if (wrapDaily && !fileSpec.Contains("[DATETIME]"))
        {
            Console.WriteLine("ERROR: Wrap daily option requires [DATETIME] in the file path.\n");
            misconfigured = true;
        }

        if (misconfigured)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine($"\t-f <LOG FILE PATH, put [DATETIME] in filename for it to be replaced by {FILE_DATE_TIME_FORMAT}>");
            Console.WriteLine("\t-p <COM PORT>");
            Console.WriteLine("\t-b <BAUD RATE>");
            Console.WriteLine($"\t-d\tWrap log file daily, [DATETIME] in filename replaced by {FILE_DATE_FORMAT}>");
            return;
        }

        MakeLogFileIfNotExists();

        while (keepRunning)
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
                checkEscapeKey();
            }

            if (!keepRunning)
            {
                return;
            }

            Console.WriteLine(" READY!");

            // Create a SerialPort instance
            using (serialPort)
            {
                try
                {
                    Console.WriteLine($"Reading data from {portName}...");
                    serialPort.ReadTimeout = 500;

                    Boolean write_time = false;
                    String text = "";

                    // Continuously read data
                    while (keepRunning)
                    {
                        checkEscapeKey();
                        // Read a line of data from the serial port
                        try
                        {
                            int data = serialPort.ReadChar();
                            char c = System.Convert.ToChar(data);
                            DateTime now = DateTime.Now;

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
                                // Append to the file with shared access
                                try
                                {
                                    // If the user has specified to wrap the log file daily, check if the date has changed
                                    if (wrapDaily && (today.ToString(FILE_DATE_FORMAT) != now.ToString(FILE_DATE_FORMAT)))
                                    {
                                        Console.WriteLine("--> Wrapping into a new log file for the day.");
                                        // Create the new log file
                                        MakeLogFileIfNotExists();
                                    }
                                    String todayText = DateTime.Now.ToString(FILE_DATE_FORMAT);
                                    using (FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                                    {
                                        using (StreamWriter writer = new StreamWriter(fs))
                                        {
                                            writer.Write(text);
                                            writer.Flush(); // Ensure data is written to the file immediately
                                            text = "";
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error: {ex.Message}");
                                    Console.WriteLine(" --- will try again later");
                                }
                                write_time = true;
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
                    }   // while (keepRunning)
                }   // try
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }   // catch
            }   // using (serialPort)
            serialPort = null;
        }   // while (keepRunning)
    }
}
