﻿
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Batch.Common;
using Microsoft.Azure.Batch.FileStaging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Web;

namespace BatchTesseractClient
{
    class Program
    {
        const string PoolName = "batchtesseractsamplepool";

        static void Main(string[] args)
        {
            Console.WriteLine("What to do?\r\n(c)reatepool\r\n(s)cheduletasks\r\n(d)elete pool");
            var actionToDo = Console.ReadLine();

            #region Reading configuration Data

            var accountRegion = ConfigurationManager.AppSettings["BatchRegion"];
            var accountName = ConfigurationManager.AppSettings["BatchAccountName"];
            var accountKey = ConfigurationManager.AppSettings["BatchAccountKey"];
            var accountBaseUrl = string.Format("https://{0}.{1}.batch.azure.com", accountName, accountRegion);

            var storageAccountName = ConfigurationManager.AppSettings["BatchDemoStorageAccount"];
            var storageAccountKey = ConfigurationManager.AppSettings["BatchDemoStorageAccountKey"];
            var storageAccount = CloudStorageAccount.Parse(string.Format(
                                    "DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}",
                                    storageAccountName,
                                    storageAccountKey));

            var blobClient = storageAccount.CreateCloudBlobClient();
            var blobOcrSourceContainer = blobClient.GetContainerReference("ocr-source");
            var blobTesseractContainer = blobClient.GetContainerReference("tesseract");

            var stagingStorageCred = new StagingStorageAccount(
                storageAccountName,
                storageAccountKey,
                string.Format("https://{0}.blob.core.windows.net", storageAccountName));

            #endregion

            Console.WriteLine("Creating batch client to access Azure Batch Service...");
            var credentials = new BatchSharedKeyCredentials(accountBaseUrl, accountName, accountKey);
            var batchClient = BatchClient.Open(credentials);
            Console.WriteLine("Batch client created successfully!");

            #region Setup Compute Pool or delete compute pool

            if (actionToDo == "c" || actionToDo == "d")
            {
                Console.WriteLine();
                Console.WriteLine("Creating pool if needed...");
                var poolExists = false;
                try
                {
                    var existingPool = batchClient.PoolOperations.GetPool(PoolName);
                    poolExists = true;
                }
                catch (Exception eq)
                {
                    poolExists = false;
                }
                if ((actionToDo == "c") && !poolExists)
                {
                    #region Get Resource Files and files to process from BLOB storage

                    Console.WriteLine();

                    var binaryResourceFiles = new List<ResourceFile>();
                    Console.WriteLine("Get list of 'resource files' required for execution from BLOB storage...");
                    foreach (var resFile in blobTesseractContainer.ListBlobs(useFlatBlobListing: true))
                    {
                        var sharedAccessSig = CreateSharedAccessSignature(blobTesseractContainer, resFile);
                        var fullUriString = resFile.Uri.ToString();
                        var relativeUriString = fullUriString.Replace(blobTesseractContainer.Uri + "/", "");

                        Console.WriteLine("- {0} ", relativeUriString);

                        binaryResourceFiles.Add(
                            new ResourceFile
                                (
                                fullUriString + sharedAccessSig,
                                relativeUriString.Replace("/", @"\")
                                )
                            );
                    }
                    Console.WriteLine();

                    #endregion

                    Console.WriteLine("Creating the pool...");
                    var newPool = batchClient.PoolOperations.CreatePool
                        (
                            PoolName,
                            "3",
                            "small",
                            25
                        );

                    

                    newPool.StartTask = new StartTask
                    {
                        ResourceFiles = binaryResourceFiles,
                        CommandLine = "cmd /c CopyFiles.cmd",
                        WaitForSuccess = true
                    };
                    newPool.CommitAsync().Wait();
                    Console.WriteLine("Pool {0} created!", PoolName);
                }
                else if ((actionToDo == "d") && poolExists)
                {
                    Console.WriteLine("Deleting the pool...");
                    batchClient.PoolOperations.DeletePoolAsync(PoolName).Wait();
                    Console.WriteLine("Pool {0} deleted!", PoolName);
                }
                else
                {
                    Console.WriteLine("Action {0} not executed since pool does {1}!",
                        actionToDo == "c" ? "'Create Pool'" : "'Delete Pool'",
                        (poolExists) ? "exist, already" : "not exist, anyway");
                }
            }

            #endregion

            #region Scheduling and running jobs

            if (actionToDo == "s")
            {
                #region Get the Task Files

                Console.WriteLine();
                var filesToProcess = new List<ResourceFile>();
                Console.WriteLine("Get list of 'files' to be processed in tasks...");
                foreach (var fileToProc in blobOcrSourceContainer.ListBlobs(useFlatBlobListing: true))
                {
                    var sharedAccessSig = CreateSharedAccessSignature(blobOcrSourceContainer, fileToProc);
                    var fullUriString = fileToProc.Uri.ToString();
                    var relativeUriString = fullUriString.Replace(blobOcrSourceContainer.Uri + "/", "");

                    Console.WriteLine("- {0}", relativeUriString);

                    filesToProcess.Add(
                        new ResourceFile(
                            fullUriString + sharedAccessSig,
                            relativeUriString.Replace("/", @"\")
                            )
                        );
                }

                #endregion

                Console.WriteLine();
                Console.WriteLine("Creating a job with its tasks...");
                var jobName = string.Format("ocr-{0}", DateTime.UtcNow.Ticks);
                Console.WriteLine("- Creating a new job {0}...", jobName);
                var ocrJob = batchClient.JobOperations.CreateJob();
                ocrJob.Id = jobName;
                ocrJob.PoolInformation = new PoolInformation { PoolId = PoolName };
                ocrJob.Commit();

                Console.WriteLine("- Adding tasks to the job of the work item.");
                var taskNr = 0;
                var job = batchClient.JobOperations.GetJob(jobName);
                foreach (var ocrFile in filesToProcess)
                {
                    var taskName = string.Format("task_no_{0}", taskNr++);

                    Console.WriteLine("  - {0} for file {1}", taskName, ocrFile.FilePath);

                    var taskCmd =
                        string.Format(
                            "cmd /c %WATASK_TVM_ROOT_DIR%\\shared\\BatchTesseractWrapper.exe \"{0}\" \"{1}\"",
                            ocrFile.BlobSource,
                            Path.GetFileNameWithoutExtension(ocrFile.FilePath));

                    var cloudTask = new CloudTask(taskName, taskCmd);

                    job.AddTask(cloudTask);
                }
                Console.WriteLine("- All tasks created, committing job!");
                job.Commit();

                Console.WriteLine();
                Console.WriteLine("Waiting for job to be completed...");
                job.Refresh();
                var stateMonitor = batchClient.Utilities.CreateTaskStateMonitor();
                stateMonitor.WaitAll(job.ListTasks(), TaskState.Completed, new TimeSpan(0, 30, 0));
                Console.WriteLine("All tasks completed!");

                var tasksFinalResult = job.ListTasks();
                foreach (var t in tasksFinalResult)
                {
                    Console.WriteLine("- Task {0}: {1}, exit code {2}", t.Id, t.State,
                        t.ExecutionInformation.ExitCode);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Press ENTER to quit!");
            Console.ReadLine();

            #endregion
        }

        static string CreateSharedAccessSignature(CloudBlobContainer blobTesseractContainer,
            IListBlobItem resFile)
        {
            var blobName = ((Microsoft.WindowsAzure.Storage.Blob.CloudBlockBlob)resFile).Name;
            var resBlob = blobTesseractContainer.GetBlockBlobReference(blobName);
            var sharedAccessPolicy = new SharedAccessBlobPolicy
            {
                Permissions = SharedAccessBlobPermissions.Read | SharedAccessBlobPermissions.List,
                SharedAccessStartTime = DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)),
                SharedAccessExpiryTime = DateTime.UtcNow.AddYears(1)
            };
            var sharedAccessSig = resBlob.GetSharedAccessSignature(sharedAccessPolicy);
            return sharedAccessSig;
        }
    }
}