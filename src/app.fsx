#if INTERACTIVE
#r "System.Xml.Linq.dll"
#r "../packages/Suave/lib/net40/Suave.dll"
#load "../packages/FSharp.Azure.StorageTypeProvider/StorageTypeProvider.fsx"
#else
module Logging
#endif
open Suave
open System
open FSharp.Data
open Suave.Filters
open Suave.Writers
open Suave.Operators
open System.Collections.Generic
open Microsoft.WindowsAzure.Storage

/// Creates a new append blob in a `<name>` container in a storage
/// account specifeid in `CUSTOMCONNSTR_THEGAMMALOGS_STORAGE` env var
let createAppendBlob name =
  let storageEnvVar = "CUSTOMCONNSTR_THEGAMMALOGS_STORAGE"
  let connStr = Environment.GetEnvironmentVariable(storageEnvVar) 
  let account = CloudStorageAccount.Parse(connStr)
  let client = account.CreateCloudBlobClient()
  let logs = client.GetContainerReference(name)
  logs.CreateIfNotExists() |> ignore
  let logName = sprintf "logs-%s.log" (DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss"))
  let appendBlob = logs.GetAppendBlobReference(logName)
  appendBlob.CreateOrReplace()
  appendBlob

// Dispatches writes to blobs based on the name of the container
let blobAgent = MailboxProcessor<_ * AsyncReplyChannel<_>>.Start(fun inbox -> async {
  let blobs = Dictionary<string, Blob.CloudAppendBlob>()
  while true do
    let! name, repl = inbox.Receive()
    if not (blobs.ContainsKey name) then blobs.Add(name, createAppendBlob name)
    repl.Reply(blobs.[name]) })

// When we get POST request to /log, write the received 
// data to the log blob (on a single line)
let app =
  setHeader  "Access-Control-Allow-Origin" "*"
  >=> setHeader "Access-Control-Allow-Headers" "content-type"
  >=> choose [
    OPTIONS >=> Successful.OK "CORS approved"
    GET >=> path "/" >=> Successful.OK "Service is running..."
    POST >=> pathScan "/log/%s" (fun name ctx -> async {
      let name = name.TrimEnd('/')
      if name |> Seq.exists (fun c -> c < 'a' || c > 'z') then 
        return! RequestErrors.BAD_REQUEST "Wrong log name" ctx
      else
        let line = System.Text.UTF32Encoding.UTF8.GetString(ctx.request.rawForm)
        let! blob = blobAgent.PostAndAsyncReply(fun ch -> name, ch)
        blob.AppendText(line + "\n")
        return! Successful.ok [||] ctx }) ]

// When port was specified, we start the app (in Azure), 
// otherwise we do nothing (it is hosted by 'build.fsx')
match System.Environment.GetCommandLineArgs() |> Seq.tryPick (fun s ->
    if s.StartsWith("port=") then Some(int(s.Substring("port=".Length)))
    else None ) with
| Some port ->
    let serverConfig =
      { Web.defaultConfig with
          logger = Logging.Loggers.saneDefaultsFor Logging.LogLevel.Warn
          bindings = [ HttpBinding.mkSimple HTTP "127.0.0.1" port ] }
    Web.startWebServer serverConfig app
| _ -> ()