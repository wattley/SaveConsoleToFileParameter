using System;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using static System.Net.Mime.MediaTypeNames;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleTitle(string lpConsoleTitle);

    static void Main(string[] args)
    {
        //string filePath = @"C:\Users\Jan\LightCat Dropbox\Projects\Trevor\VoidDetect\Calculations and Data\20241223 Console Output.txt";
        //string portName = "COM5"; // Replace with your port name
        //int baudRate = 115200; // Replace with your baud rate
        string filePath = "";
        string portName = "";
        int baudRate = 0;
        int haveParameterFlags = 0;
        const int HAVE_PORT = 1, HAVE_FILE_PATH = 2, HAVE_BAUD_RATE = 4;
        SerialPort serialPort = null;
        const String FILE_DATE_TIME_FORMAT = "yyMMddHHmm";
        const String LOG_DATE_TIME_FORMAT = "yyyy-MM-dd HH:mm:ss";

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
                filePath = args[i + 1];
                haveParameterFlags |= HAVE_FILE_PATH;
            }
            else if (args[i] == "-b" && i + 1 < args.Length)
            {
                Int32.TryParse(args[i + 1], out baudRate);
                haveParameterFlags |= HAVE_BAUD_RATE;
            }
        }

        if (haveParameterFlags != (HAVE_PORT + HAVE_FILE_PATH + HAVE_BAUD_RATE))
        {
            Console.WriteLine("Usage:");
            Console.WriteLine($"\t-f <LOG FILE PATH, put [DATETIME] in filename for it to be replaced by {FILE_DATE_TIME_FORMAT}>");
            Console.WriteLine("\t-p <COM PORT>");
            Console.WriteLine("\t-b <BAUD RATE>");
            return;
        }

        filePath = filePath.Replace("[DATETIME]", DateTime.Now.ToString(FILE_DATE_TIME_FORMAT));

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

        Boolean keepRunning = true;
        int retryCount = 2 * 30;    // Try for 30 seconds
        Console.Write($"Opening {portName} ");
        while (serialPort == null && retryCount > 0)
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
                // Console.WriteLine($"Error: {ex.Message}");
                serialPort.Dispose();
                serialPort = null;
                retryCount--;
                System.Threading.Thread.Sleep(500);
            }
        }

        if (serialPort == null)
        {
            Console.WriteLine("Error: Could not open serial port");
            return;
        }
        else
        {
            Console.WriteLine(" READY!");
        }

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
                    if (Console.KeyAvailable)
                    {
                        ConsoleKey key = Console.ReadKey(intercept: true).Key;
                        keepRunning = (key != ConsoleKey.Escape);
                    }
                    // Read a line of data from the serial port
                    try
                    {
                        int data = serialPort.ReadChar();
                        char c = System.Convert.ToChar(data);

                        if (write_time && (c != '\r') && (c != '\n'))
                        {
                            write_time = false;
                            DateTime now = DateTime.Now;
                            String now_text = now.ToString(LOG_DATE_TIME_FORMAT) + ": ";
                            Console.Write(now_text);
                            text = now_text;
                        }

                        // Display the data on the console
                        Console.Write(c);

                        // Write the data to the file
                        text += c;

                        if ((c == '\n') || (c == '\r'))
                        {
                            // Append to the file with shared access
                            using (FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                            using (StreamWriter writer = new StreamWriter(fs))
                            {
                                writer.Write(text);
                                writer.Flush(); // Ensure data is written to the file immediately
                                text = "";
                            }
                            write_time = true;
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Ignore timeout exceptions
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
