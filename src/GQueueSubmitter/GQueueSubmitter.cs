using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;


namespace GQueueSubmitter
{   
    // Utility class similar to the one used in GQueue
    public static class Utility
    {
        // Utility to get the executable path in order to establish the location of the required program files (by default in the same directory as the executable)
        public static string GetExecutablePath()
        {
            string exePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (exePath == null)
            {
                Console.WriteLine("Unable to determine the GQueueSubmitter executable path! Terminating...");
                Environment.Exit(1);
            }
            return exePath;
        }
    }
    // A class to generate unique job IDs
    public class IdGenerator
    {
        private static readonly string IdFilePath; // Path to the file where the IDs are stored
        private static readonly object fileLock = new object(); // Object for thread synchronization

        static IdGenerator()
        {
            string exePath = Utility.GetExecutablePath();
            IdFilePath = Path.Combine(exePath, "jobs_ids.log");
        }

        // Method to generate the next ID incrementally in a thread-safe manner
        public int GetNextId()
        {
            lock (fileLock) // Ensure thread safety within the same process
            {
                int currentRetry = 0;
                const int maxRetries = 10;
                while (currentRetry < maxRetries) // Attempt to access the jobs_ids.log file up to 10 times
                {
                    try
                    {
                        int nextId = 1;
                        // Try to open the file exclusively
                        using (var fs = new FileStream(IdFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        {
                            // Read the contents of the jobs_ids.log file
                            using (var reader = new StreamReader(fs))
                            {
                                string idText = reader.ReadToEnd().Trim();
                                // Parse the read value, conver it to an integer
                                if (int.TryParse(idText, out int lastId))
                                {
                                    nextId = lastId + 1;
                                }
                            }
                        }
                        // Write the new ID to the file:
                        using (var writer = new StreamWriter(new FileStream(IdFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None)))
                        {
                            writer.Write(nextId.ToString());
                        }
                        return nextId; // Success, exit the loop and return the ID.
                    }
                    catch (IOException)
                    {
                        // The file is probably locked, wait for a short period and try again.
                        currentRetry++; // Increment the maximum retry counter
                        Thread.Sleep(100); // Wait for 100 ms 
                    }
                }
                throw new Exception("Failed to get the next ID after several retries.");
            }
        }
    }

    // The main class handling job queue management
    class JobQueueManager
    {
        private static readonly string QueueFilePath; // Path to the jobs_queued.que file
        private static readonly object fileLock = new object(); // Object for syncing file access
        private static IdGenerator _idGenerator = new IdGenerator(); // Create a new instanc eof the class that generates unique job IDs

        // Static constructor to generate the path to the jobs_queued.que file
        static JobQueueManager()
        {
            string exePath = Utility.GetExecutablePath();
            QueueFilePath = Path.Combine(exePath, "jobs_queued.que");
        }

        // Entry point to the program, process terminal commands "submit" and "remove" job
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: <command> [options]");
                return;
            }

            string command = args[0].ToLower();

            switch (command)
            {
                case "submit":
                    if(args.Length !=3)
                    {
                        Console.WriteLine("Usage: submit <path_to_job_file> <priority>");
                        return;
                    }
                    SubmitJob(args.Skip(1).ToArray());
                    break;

                case "remove":
                    if(args.Length !=2)
                    {
                        Console.WriteLine("Usage: remove <job_id>");
                        return;
                    }
                    RemoveJob(new string[] { args[1]});
                    break;
                default:
                    Console.WriteLine($"Unknown command: {command}");
                    break;
            }
        }

        // Method that handles the submission of a job to the queue to be picked up by the GQueue daemon
        private static void SubmitJob(string[] args) { 
            string jobFilePath = args[0];
            if (!File.Exists(jobFilePath))
            {
                Console.WriteLine("Error: Job file does not exist.");
                return;
            }
            // Check if the priority is between 1 and 10 (1 - lowest priority, 10 - highest priority)
            if (!int.TryParse(args[1], out int jobPriority) || jobPriority < 1 || jobPriority > 10)
            {
                Console.WriteLine("Error: Priority must be an integer between 1 and 10.");
                return;
            }

            // Extract job details from the file
            (int nProcs, int mem) = ExtractDetailsFromJobFile(jobFilePath);

            // Generate the next ID
            int jobId = _idGenerator.GetNextId();

            // Prepare the job entry
            string jobEntry = $"#{jobId} {jobFilePath} {jobPriority} {DateTime.Now:dd/MM/yyyy HH:mm} {nProcs} {mem}";

            // Write to the jobs.que file in a thread-safe manner
            WriteJobToQueue(jobEntry);
        }

        // Method that parses the number of processors and memory requested for the job submitted for Gaussian calculation
        public static (int nProcs, int mem) ExtractDetailsFromJobFile(string jobFilePath)
        {
            var content = File.ReadAllText(jobFilePath);

            // Default unless specified otherwiese in the job file
            int nProcs = 1; 
            int mem = 1;    

            // Look for %nprocshared or %nprocs patterns in the input file
            var nprocsMatch = Regex.Match(content, @"%nprocs?hared?=(\d+)");
            if (nprocsMatch.Success)
            {
                nProcs = int.Parse(nprocsMatch.Groups[1].Value);
            }
            else
            {
                // Look for %cpu patterns in the input file
                var cpuMatch = Regex.Match(content, @"%cpu=(.*)");
                if (cpuMatch.Success)
                {
                    var cpuStr = cpuMatch.Groups[1].Value;
                    if (cpuStr.Contains('-'))
                    {
                        // Gaussian allows that every other core is allocated to a job, i.e., %cpu = 0-10/2
                        if (cpuStr.Contains('/'))
                        {
                            var parts = cpuStr.Split(new[] { '-', '/' });
                            int start = int.Parse(parts[0]);
                            int end = int.Parse(parts[1]);
                            int step = int.Parse(parts[2]);
                            nProcs = ((end - start) / step) + 1;
                        }
                        else
                        // Or the CPUs can dedicated in a continuous fashion
                        {
                            var parts = cpuStr.Split('-');
                            int start = int.Parse(parts[0]);
                            int end = int.Parse(parts[1]);
                            nProcs = end - start + 1;
                        }
                    }
                    else if (cpuStr.Contains(','))
                    {
                        nProcs = cpuStr.Split(',').Length;
                    }
                    else
                    {
                        nProcs = int.Parse(cpuStr);
                    }
                }
            }

            // Extract the memory required by the job, GQueueSubmitter expects the value to be in GB
            var memMatch = Regex.Match(content, @"%mem=(\d+)GB");
            if (memMatch.Success)
            {
                mem = int.Parse(memMatch.Groups[1].Value);
            }
            // Debug output to the console:
            Console.WriteLine($"Parsed nProcs: {nProcs}");
            Console.WriteLine($"Parsed mem: {mem}GB");

            return (nProcs, mem);
        }

        // Method to remove jobs from the jobs_queued.que file upon the user's request
        private static bool RemoveJob(string[] args)
        {
            if (args.Length != 1 || !int.TryParse(args[0], out int jobId))
            {
                Console.WriteLine("Usage: remove <jobId>");
                return false;
            }

            try
            {
                // Open the file with a FileStream using FileShare.None to prevent other processes from accessing the file
                using (var stream = new FileStream(QueueFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var reader = new StreamReader(stream);
                    var allJobs = reader.ReadToEnd().Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();

                    bool jobRemoved = allJobs.RemoveAll(job => job.StartsWith($"#{jobId} ")) > 0;

                    if (jobRemoved)
                    {
                        stream.SetLength(0); // Truncate the file
                        stream.Seek(0, SeekOrigin.Begin); // Move to the beginning of the file

                        var writer = new StreamWriter(stream);
                        writer.Write(string.Join(Environment.NewLine, allJobs));
                        writer.Flush(); // Ensure all data is written to the file
                        Console.WriteLine($"Job #{jobId} has been removed from the queue successfully.");
                    }
                    else
                    {
                        Console.WriteLine($"Job #{jobId} was not found in the queue!.");
                    }
                    return jobRemoved;
                }
            }
            catch (IOException)
            {
                // Handle the case where the file is locked by another process
                Console.WriteLine("Job queue is currently in use. Please try again later.");
                return false;
            }
        }

        // Method to write the updated jobs_queued.que file
        private static void WriteJobToQueue(string jobEntry)
        {
            const int maxRetries = 10;
            const int delayBetweenRetries = 100; // milliseconds
            int attempt = 0;

            // Attempt to write to the jobs_queued.que file up to 10 times
            while (true)
            {
                try
                {
                    lock (fileLock)
                    {
                        // Attempt to write to the file
                        File.AppendAllText(QueueFilePath, jobEntry + Environment.NewLine);
                        break; // Success, exit the loop
                    }
                }
                catch (IOException ex)
                {
                    attempt++;
                    if (attempt >= maxRetries)
                    {
                        // If reached the max number of retries, rethrow the exception
                        throw new IOException($"Failed to write to the queue file after {maxRetries} attempts.", ex);
                    }

                    // Wait a bit before retrying
                    Thread.Sleep(delayBetweenRetries); // Wait for 100 ms
                }
            }
        }
    }
}
