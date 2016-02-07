#r "System.Xml.Linq.dll"
#r "packages/FAKE/tools/FakeLib.dll"
#r "packages/Suave/lib/net40/Suave.dll"
#r "packages/FSharp.Data/lib/net40/FSharp.Data.dll"
#load "packages/FSharp.Azure.StorageTypeProvider/StorageTypeProvider.fsx"
open Fake
open Suave
open System
open FSharp.Data
open Suave.Filters
open Suave.Operators
open Microsoft.WindowsAzure.Storage

/// Creates a new append blob in the `logs` container in a storage
/// account specifeid in `CUSTOMCONNSTR_COEFFECTLOGS_STORAGE` env var
let createAppendBlob () =
  let storageEnvVar = "CUSTOMCONNSTR_COEFFECTLOGS_STORAGE"
  let connStr = Environment.GetEnvironmentVariable(storageEnvVar) 
  let account = CloudStorageAccount.Parse(connStr)
  let client = account.CreateCloudBlobClient()
  let logs = client.GetContainerReference("logs")
  logs.CreateIfNotExists() |> ignore
  let logName = sprintf "logs-%s.log" (DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss"))
  let appendBlob = logs.GetAppendBlobReference(logName)
  appendBlob.CreateOrReplace()
  appendBlob

// Create a blob to be used for all logging
let blob = createAppendBlob()

// When we get POST request to /log, write the received 
// data to the log blob (on a single line)
let app =
  choose [
    path "/" >=> Successful.OK "Service is running..."
    path "/log" >=> POST >=> request (fun req ->
      let line = System.Text.UTF32Encoding.UTF8.GetString(req.rawForm)
      let line = line.Replace('\n',' ').Replace('\r',' ')
      blob.AppendText(line + "\n")
      Successful.ok [||]) ]

// When port was specified, we start the app (in Azure), 
// otherwise we do nothing (it is hosted by 'build.fsx')
match Int32.TryParse(getBuildParam "port") with
| true, port ->
    let serverConfig =
      { Web.defaultConfig with
          logger = Logging.Loggers.saneDefaultsFor Logging.LogLevel.Warn
          bindings = [ HttpBinding.mkSimple HTTP "127.0.0.1" port ] }
    Web.startWebServer serverConfig app
| _ -> ()