using System;
using System.Diagnostics; // Process and system diagnostics
using System.IO; // For input and output operations
using System.Collections.Generic; // For List<> collections
using System.Linq; // Fata manipulation
using System.Linq.Expressions; // For lambda expressions
using System.Text.RegularExpressions; // For regular expressions
using System.Reflection; // Working with assembly and its metadata

namespace GQueue
{
    // Job class that represents each job in the queue:
    public class Job
    {
        public int JobId { get; set; } // Property to store the unique job (calculation) ID
        public string JobPath { get; set; } // Property to store the job's file path
        public int JobPriority { get; set; } // Property for job priority
        public DateTime JobTimeSubmitted { get; set; } 
        public int NProcs { get; set; } // Property to store the number of processors (cores) required for the jo
        public int Mem { get; set; } // Property to store the RAM required for the job in GB
        public int Pid { get; set; } // Property to store the Screen process ID
    }

    // Utility class providing helper methods for Gaussian script execution and path determination:
    public static class Utility
    {
        // Method to execute the Bash commands
        public static string ExecuteBashCommand(string command)
        {
            var escapedArgs = command.Replace("\"", "\\\""); // Escaping quotes in the command

            // Create a new process 
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash", // Use Bash to run commands
                    Arguments = $"-c \"{escapedArgs}\"", // Pass the command as argument
                    RedirectStandardOutput = true, // Supress the output of the command to the terminal or otherwise
                    UseShellExecute = false,
                    CreateNoWindow = true, // Don't create a new window 
                    RedirectStandardError = true // Supress any error outputs of the ran process
                }
            };
            try
            {
                process.Start(); // Start the process
                string result = process.StandardOutput.ReadToEnd(); // Read the output of the ran Bash command
                process.WaitForExit();
                return result.Trim(); // Remove white space before and after the string
            }
            catch (Exception ex)
            {
                ErrorLogger.LogError(ex); // Log the errors to the GQueue log
                return string.Empty; // Return an empty string if an error occurs
            }
        }

        // Method to get the executable path in order to establish the location of the required program files (by default in the same directory as the executable)
        public static string GetExecutablePath()
        {
            string exePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (exePath == null)
            {
                ErrorLogger.LogError("Unable to determine the GQueue executable path! Terminating...");
                Environment.Exit(1); // Exit the program with a non-zero status, thus indicating an error
            }
            return exePath;
        }
    }

    // Obtain system CPU and RAM info:
    public static class SystemResources
    {
        // Method to determine the number of CPU cores available in the system
        public static int GetAvailableCpuCores()
        {
            var BashOutput = Utility.ExecuteBashCommand("nproc");
            int.TryParse(BashOutput, out int availableCores);
            return availableCores;
        }

        // Method to determine the RAM available in the system, returns the value in GB
        public static int GetAvailableRamMemory()
        {
            // Obtain the available memory in kb and convert to GB:
            var BashOutput = Utility.ExecuteBashCommand("grep -i MemAvailable /proc/meminfo | awk '{print $2}'");
            int.TryParse(BashOutput, out int availableMemKb);
            return availableMemKb / (1024 * 1024); // convert KB to GB
        }
    }

    // Class for error and debugging logging
    public static class Logger
    {            
        private static readonly string LogFileName = "jobs_processed.que"; // Default name for the completed jobs log file
        private static string LogFilePath;

        // Static constructor to initialize static members of the class
        static Logger()
        {
            string exePath = Utility.GetExecutablePath(); // Determine the directory path of the parent GQueue executable
            LogFilePath = Path.Combine(exePath, LogFileName);
        }

        // Method to determine remaining system resources and log the start of a calculation in the jobs_processed.que log file
        public static void LogJobStart(Job job, int processId, string screenSessionName)
        {
            // Calculate remaining resources:
            int remainingCpuCores = SystemResources.GetAvailableCpuCores() - ResourceTracker.UsedCpuCores;
            int remainingRam = SystemResources.GetAvailableRamMemory() - ResourceTracker.UsedMemory;

            // The string which will be written to the jobs_processed.que log file
            string logEntry = $"{DateTime.Now}: Job {job.JobPath} started in screen session {screenSessionName} with PID {processId}," +
                $"using {job.NProcs} CPU cores and {job.Mem} GB of RAM. Remaining {remainingCpuCores} CPU cores and {remainingRam} GB of RAM.";

            try
            {
                // Append the string to the end of the log file:
                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                ErrorLogger.LogError(ex);
            }
        }

        // Method to log that an instance of Gaussian has terminated in the corresponding Screen session
        public static void LogJobTerminated(Job job, int processId, string screenSessionName)
        {
            string logEntry = $"{DateTime.Now}: Job{job.JobPath} corresponding to PID {processId} was terminated" +
                $" along with screen session {screenSessionName}";

            try
            {
                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                ErrorLogger.LogError(ex);
            }
        }

        // Method to log additional custom strings for debuggin purposes
        public static void LogDebug(string message)
        {
            string logEntry = $"{DateTime.Now}: DEBUG: {message}";
            try
            {
                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                ErrorLogger.LogError(ex);
            }
        }
    }

    // The class that handles the reading of the queue file and 
    public class JobQueue
    {
        private List<Job> _jobs = new List<Job>(); // _jobs collection to which jobs read from the queue file are added and pending start
        private List<Job> _jobsRunning = new List<Job>(); // _jobsRunning collection to which the started jobs are added

        // Construct the path to the queue file:
        private static string exePath = Utility.GetExecutablePath();
        private static string QueueFilePath = Path.Combine(exePath, "jobs_queued.que");

        // Use en-GB for the formatting of dates and numbers
        private readonly System.Globalization.CultureInfo _cultureInfo = new System.Globalization.CultureInfo("en-GB");

        // Object used for thread sync
        private static readonly object fileLock = new object();

        // Constructor of the JobQueue class
        public JobQueue()
        {
            // Load jobs from the queue
            LoadJobsFromQueueFile(); 
        }

        // Method to load queued files from jobs_queued file to which GQueueSubmitter wrote:
        private void LoadJobsFromQueueFile()
        {
            try
            {
                // Check if the jobs_queued.que file exists
                if (File.Exists(QueueFilePath))
                {
                    // Read the contents of the jobs_queued.que file
                    var lines = File.ReadAllLines(QueueFilePath);
                    foreach (var line in lines)
                    {
                        // The expected separators in the jobs_queued.que file are spaces:
                        var parts = line.Split(' ');
                        if (parts.Length < 7) continue;

                        // Parse the first part of the line as an integer after removing the leading #1. The number is generated by a method in GQueueSubmitter
                        var jobId = int.Parse(parts[0].TrimStart('#')); // get the JobId
                        // Check if a job with the same jobId does not already exists in _jobs collection
                        if (!_jobs.Any(j => j.JobId == jobId))
                        {
                            // Create a new jobs object and initialize its properties from the parsed line in the jobs_queued.que file
                            var job = new Job
                            {
                                JobId = int.Parse(parts[0].TrimStart('#')), // jobId
                                JobPath = parts[1], // Path to the job file, GQueueSubmitter checks if the file exists first
                                JobPriority = int.Parse(parts[2]), // Job priority
                                JobTimeSubmitted = DateTime.ParseExact(parts[3] + " " + parts[4], "dd/MM/yyyy HH:mm", _cultureInfo), // Expected format of the DateTime object
                                NProcs = int.Parse(parts[5]), // Number of processor cores requested for the job
                                Mem = int.Parse(parts[6]) // Memory (expected in GB) for the job
                            };
                            _jobs.Add(job); // Add the job to the _jobs collection
                        }
                    }
                    // After reading all the queued jobs, delete the contents of the jobs_queued.que file
                    File.WriteAllText(QueueFilePath, string.Empty);
                }
            }
            catch (Exception ex)
            {
                ErrorLogger.LogError(ex);
            }
        }

        // Method to continuously monitor and process jobs in the queue
        public void ProcessAndMonitorJobs()
        {
            while (true)  // Run perpetually
            {
                lock (fileLock) // Ensure thread safety
                {
                    LoadJobsFromQueueFile(); // Reload jobs from file to detect if the queue is updated

                    int availableCpuCores = SystemResources.GetAvailableCpuCores();
                    int availableRam = SystemResources.GetAvailableRamMemory();

                    // Select jobs that can be run with the available resources and sort them by priority and submission time if the priority is the same
                    List<Job> jobsToRun = _jobs
                        .Where(j => j.NProcs <= availableCpuCores && j.Mem <= availableRam) // Filter out which jobs cannot be run due to resource constraints
                        .OrderByDescending(j => j.JobPriority) // Order by descending order of priority, 10 is the highest, 1 is the lowest priority
                        .ThenBy(j => j.JobTimeSubmitted) // If there are jobs if the same priority, sort them by the date and time of submission
                        .ToList();

                    foreach (Job job in jobsToRun)// Iterate through the jobs that are in the queue
                    {
                        StartJob(job);
                        // Add the job to the running jobs list:
                        _jobsRunning.Add(job);

                        // Remove the list from the queued list:
                        _jobs.Remove(job);
                    }
                    RemoveUnusedScreenSessions(); // Call the method to delete the unused screen sessions
                }
                Thread.Sleep(10000); // Pause the loop for 10 seconds before the next iteration
            }
        }

        // Method to start jobs using an external script
        private void StartJob(Job job)
        {
            // Increment the used CPU cores and RAM from the submitted job
            ResourceTracker.UsedCpuCores += job.NProcs;
            ResourceTracker.UsedMemory += job.Mem;

            // Use the JobId as the unique Screen session name
            var screenSessionName = $"job_{job.JobId}";
            // Script location:
            string exePath = Utility.GetExecutablePath();

            // Construct the path to the script
            var scriptPath = Path.Combine(exePath, "run_gaussian.sh");

            var gaussianScriptCommand = $"{scriptPath} {job.JobPath}";
            var startJobCommand = $"screen -dmS {screenSessionName} bash -c '{gaussianScriptCommand} ; exec bash'";

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{startJobCommand}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            try
            {
                process.Start();
                job.Pid = process.Id; // Store the PID of the now running Gaussian job in the job object

                // Write to the jobs log:
                Logger.LogJobStart(job, process.Id, screenSessionName);
            }
            catch (Exception ex)
            {
                ErrorLogger.LogError(ex);
            }
        }

        private void RemoveUnusedScreenSessions()
        {
            // Debugging:
            //Logger.LogDebug("Starting to check for unused screen session.");
            //Logger.LogDebug($"Number of jobs in the list: {_jobs.Count}");

            foreach (var job in _jobsRunning.ToList())
            {
                // Construct the expected path for the .done file (touch method) which is created when the Gaussian job is complete
                string jobDoneFilePath = Path.Combine(Path.GetDirectoryName(job.JobPath), Path.GetFileNameWithoutExtension(job.JobPath) + ".done");

                // Debugging: print out the contents of the jobDirectory folder
                /*
                string jobDirectory = Path.GetDirectoryName(job.JobPath);
                if (Directory.Exists(jobDirectory))
                {
                  var files = Directory.GetFiles(jobDirectory);
                  Logger.LogDebug($"Files in directory {jobDirectory}: {string.Join(", ", files)}");
                }
                */

                // Check if the .done file exists
                if (File.Exists(jobDoneFilePath))
                {
                    Logger.LogDebug($"Found .done file for Job ID {job.JobId}, removing job and freeing resources.");

                    // Free up resources used by the completed job
                    FreeUpResources(job);

                    // Terminate the associated screen session
                    var screenSessionName = $"job_{job.JobId}";
                    TerminateScreenSession(screenSessionName);

                    // Log job termination
                    Logger.LogJobTerminated(job, job.Pid, screenSessionName);

                    // Remove the job from the running jobs list
                    _jobsRunning.Remove(job);

                    // Delete the .done file after processing
                    File.Delete(jobDoneFilePath);
                }
                else
                {
                    // Debugging:
                    //Logger.LogDebug($".done file not found for Job ID {job.JobId}");
                }
            }
            // Debugging:
            //Logger.LogDebug("Finished checking for unused screen sessions.");
        }

        // Method to clear up system resources after a job was terminated
        private void FreeUpResources(Job job)
        {
            ResourceTracker.UsedCpuCores -= job.NProcs;
            ResourceTracker.UsedMemory -= job.Mem;

            // Ensure values don't drop below zero
            ResourceTracker.UsedCpuCores = Math.Max(0, ResourceTracker.UsedCpuCores);
            ResourceTracker.UsedMemory = Math.Max(0, ResourceTracker.UsedMemory);
        }

        // Quit the Screen session associated with the unique job
        private void TerminateScreenSession(string screenSessionName)
        {
            Utility.ExecuteBashCommand($"screen -S {screenSessionName} -X quit");
        }
    }

    // Class to store the available system resources for the jobs
    public static class ResourceTracker
    {
        public static int UsedCpuCores = 0;
        public static int UsedMemory = 0;
    }

    // Class used to log exceptions and custom strings for debugging purposes
    public static class ErrorLogger
    {
        private static string exePath = Utility.GetExecutablePath();
        private static string ErrorLogFilePath = Path.Combine(exePath, "errors.log");

        // Log exceptions
        public static void LogError(Exception ex)
        {
            Log($"{DateTime.Now}: Exception: {ex}");
        }

        // Log custom message for debugging purposes
        public static void LogError(string message)
        {
            Log($"{DateTime.Now}: Error message: {message}");
        }

        private static void Log(string logEntry)
        {
            try
            {
                File.AppendAllText(ErrorLogFilePath, logEntry + "\n");
            }
            catch
            {
                //Not much to do if logging fails...
            }
        }
    }

    // The entry point to the program:
    static class GQueue
    {
        private static JobQueue jobQueue = new JobQueue();

        static void Main(string[] args)
        {
            try
            {
                // Initialize and start the job scheduler and monitor.
                jobQueue.ProcessAndMonitorJobs();
            }

            catch (Exception ex)
            {
                ErrorLogger.LogError(ex);
            }
        }
    }
}
