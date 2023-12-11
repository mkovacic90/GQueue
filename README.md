# About GQueue

GQueue is a specialized, easy to use job scheduling and monitoring tool for single machines running computational chemistry calculations, particularly in the [Gaussian®](https://gaussian.com/gaussian16/) program suite. 
GQueue dynamically allocates resources for queued calculations based on the user’s job requirements and designated job priority, aiming to ensure efficient utilization of computational resources. 
GQueueDaemon, and GQueueSubmitter, are free to use, open-source programs distributed under the Apache License 2.0. Under the Apache License 2.0, users are permitted to freely use, modify, and distribute this software, both in original and modified forms for any purpose. However, they must not use the name of the project or its contributors to endorse or promote products derived from this software without prior written permission.  

**Note**: Users who wish to use GQueue for the intended purpose of managing jobs in Gaussian® should ensure that they own a legitimate, licensed copy of Gaussian® itself. GQueue does not provide Gaussian®, which is a propriety software of Gaussian, Inc. GQueue is in no way affiliated, supported nor endorsed by Gaussian, Inc.  
 
# Requirements and installation
In order to use GQueue (and its helper GQueueSubmitter) program code as it is provided on GitHub, the user should have a running Linux machine with .NET 6 installed and a licensed copy of Gaussian®. This application was built and tested in OpenSUSE 15.2. However, it is more than likely that the original code can also run in another distribution which has .NET available in its official repositories (CentOS, Debian, Fedora, Ubuntu and possibly others). 
Additionally, it’s important to note that users should have access to the administrative (sudo) rights of the system. This is necessary to perform the installation of .NET, for managing system services and configuring the user and group rights of the application and associated program files.

## Installation 
An abbreviated (TL;DR) checklist of the necessary steps in order to run the GQueue daemon and GQueueSubmitter, assuming that Gaussian® is already installed and configured properly, is as follows:
 * Install the .NET 6.0 framework,
 *	Compile GQueueDaemon and GQueueSubmitter using the .NET compiler,  
 *	Set necessary user and group ownerships of GQueueDaemon, GQueueSubmitter, and their corresponding program files, along with helper files jobs_queued.que, jobs_processed.que, jobs_ids.log, errors.log (create these as blank text files in the parent directory) to belong to the same user and group that own the Gaussian® installation. Set the executable permissions for GQueueDaemon and GQueueSubmitter,  
 *	Configure GQueueDaemon as a background daemon service and reload the daemon,  
 *	Configure the run_gaussian.sh script file necessary to run Gaussian®.
And that’s it! Congratulations, you are now ready to use GQueue.
 
### Step-by-step installation on an OpenSUSE 15.x system
Below are step-by-step instructions how to compile and configure GQueue, which are valid for OpenSUSE 15.x. Adjust accordingly to your distribution’s specific guidelines.
#### Step 1. Install the .NET 6.0 SDK
 i)	Add the Microsoft package repository. Open a Bash terminal and execute:
 ```
 sudo zypper addrepo --check --name 'Microsoft' --type RPM-MD https://packages.microsoft.com/yumrepos/microsoft-opensuse15-prod microsoft-opensuse15-prod
 ```
 ii)	Update the package lists:
 ```
 sudo zypper update
 ```
 iii)	Install the .NET 6.0 SDK:
 ```
 sudo zypper install dotnet-sdk-6.0
 ```
 iv)	Verify the .NET installation:
 ```
 dotnet --version
 ```
The terminal should return the .NET version installed.

#### Step 2. Compile GQueueDaemon and GQueueSubmitter source code with the .NET compiler
i)	Use a file extractor (Ark for example) to extract the ZIP file from the GitHub repository into an appropriate directory, e.g., /home/GQueue.   
ii)	Navigate to the target /src subdirectories,namely GQueueDaemon and GQueueSubmitter, e.g.:
```
cd /home/GQueue/GQueue-main/src/GQueueSubmitter
```  
iii)	Compile the GQueue and GQueueSubmitter code from the respective .csproj files in their corresponding directories:
```
dotnet build -o bin GQueueSubmitter.csproj
```  
This command will compile the GQueueSubmitter code and place it in the bin directory within the parent directory. In Linux, the executable has no extension. This file along with the corresponding runtime configuration settings (.runtimeconfig.json), dependencies (.deps.json) and .dll files is required to run the application. Copy these files to the parent GQueue directory, the .pdb file can be omitted.
Make sure to repeat the same procedure for GQueueDemon as well.  

#### Step 3. Set necessary permissions for both executables, their dependencies and register GQueueDaemon as a system service  
i)	Make sure that the GQueueDaemon and GQueueSubmitter have necessary permissions and belong, along with their required files from the previous step, to the user and group as Gaussian®. In addition, make sure that both executables have set execute permissions. This can be done in the GUI, by right clicking the files and setting the Permissions, or by using terminal commands ```chmod``` and ```chown```.  
ii)	Create required program files in the parent GQueue directory: ```jobs_queued.que```, ```jobs_processed.que```, ```jobs_ids.log``` and ```errors.log```. Create these as blank text files with the appropriate extensions. Set the ownership of these files to belong to the same user and group as GQueueDaemon and GQueueSubmitter.

#### Step 4. Configure GQueueDaemon as a background service  
i)	Use a text editor, for example Nano, to create a new service file:
sudo nano /etc/systemd/system/gqueue.service
Add the following configuration to the service file (adjust paths, user and group as necessary):
```
[Unit]
Description=GQueue Job Scheduler and Monitor

[Service]
# Make sure to set the correct folder for both the .NET and GQueueDaemon
ExecStart=/usr/bin/dotnet /home/user/GQueue/GQueueDaemon.dll
WorkingDirectory=/home/user/GQueue
# Make sure to set the correct user and group
User=user
Group=group
Restart=always
# Restart the service after 10 seconds if the dotnet service crashes
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=gqueue
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```
Save the changes and exit the text editor.
ii)	Reload the system manager configuration:
```
sudo systemctl daemon-reload
```
Enable the service to start on boot:
```
sudo systemctl enable gqueue.service
```
Start the service:
```
sudo systemctl start gqueue.service
```
Verify that the service is running properly:
```
sudo systemctl status gqueue.service
```

## Step 5. Configure the script file run_gaussian.sh  
i)	Create a new run_gaussian.sh script file in the same parent directory. Add add the following to the script (adjust paths and Gaussian® versions as necessary):
```#!/bin/bash

# Load Gaussian environment variables, adjust path accordingly
source /home/user/g16/bsd/g16.profile

# Execute Gaussian with provided job file path
/home/gaussian/g16/g16 "$1"

# After the job is complete, create an unique flag file from the job file name
job_identifier=$(basename "$1" .gjf) # Assuming the job has a .gjf extension, modify if using .com extension
flag_file_path="$(dirname "$1")/${job_identifier}.done"

# Create the flag file
touch "$flag_file_path"
```
Set the same user and group ownership as previously for the executables.
 
# How to use GQueue  
## Submitting a job to the queue 
To submit a job (.gjf file) to the GQueue manager, navigate to the GQueueSubmitter parent folder first and use the following command in the Bash terminal:
```
dotnet ./GQueueSubmitter.dll submit <path_to_job_file> <priority>
```
Replace the ```<path_to_job_file>``` with the corresponding full path to your job file (i.e., /home/user/g16/jobs/test_job_1.gjf) and replace <priority> with an integer value ranging from 1 to 10. “1” corresponds to the lowest job priority, whereas “10” is the highest priority. 
Example:
```
dotnet ./GQueueSubmitter.dll submit /home/user/g16/jobs/test_job_1.gjf 10
```
Note: the job file name shouldn’t include blank spaces! Use underscore or hyphen symbols instead. 
If the job is successfully submitted to the queue, GQueueSubmitter will return the parsed number of processor cores and allocated RAM from the read job file and will add the job to the “jobs_queued.que” file. GQueueSubmitter can parse the %nproc, %nprocshared and %cpu inputs in the .gjf file.  
GQueueDaemon periodically checks the aforementioned “jobs_queued.que” file, and when a new queued job entry is found, will run the calculation depending on the number of jobs queued previously, their priorities and available system resources. 

## Removing a job from the queue 
If you would like to remove a queued file, this can be done with the following command:  
```
dotnet ./GQueueSubmitter.dll remove <job_id>.
```
The corresponding <job_id> can be found in the “jobs_queued.que” file, and is a single integer value after the leading “#” symbol.  
Example:
```
#48 /home/user/g16/jobs/test_job_1.gjf 10 10/12/2023 17:58 2 4
```
The ```<job_id>``` of the queued job in question is 48. The clarify the “jobs_queued.que” file further, the path to the job file is home/user/g16/jobs/test_job_1.gjf, the priority of the job is 10, the job was queued on 10 December 2021 at 5:28 PM. The job requires 2 CPU cores and 4 GB of RAM.
Please note that the files are automatically removed from the queue file once they are started in Gaussian®. If you want to terminate a job that has started already, locate the job’s Screen session ID and PID within the “jobs_processed.que” file. You can either log into the Screen session to kill the Gaussian calculation or kill the corresponding Screen PID.

