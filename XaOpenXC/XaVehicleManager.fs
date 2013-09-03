namespace XaOpenXC

open System
open System.Collections.Generic
open System.Linq
open System.Text
open System.IO
open System.Threading
open Logging

open Android.App
open Android.Content
open Android.OS
open Android.Runtime
open Android.Views
open Android.Widget

type private ReaderAgentMsg = 
    | ConnectorStateChanged of ConnectorState 
    | Register of string*MailboxProcessor<string> 
    | Unregister of string
    | Close of AsyncReplyChannel<unit>
    | Write of byte array
    
module private messaging =

    let safeReadStream (stream:Stream) (buffer:byte array) = try stream.Read(buffer,0,buffer.Length) with _ -> 0
    
    ///generate a sequence of raw 512 max chunks from a stream
    let rawReadSeq (stream:Stream option ref) (signal:ManualResetEvent) =
        seq {
            let buffer = Array.create 512 0uy
            while true do
                signal.WaitOne() |> ignore
                match !stream with
                | None -> ()
                | Some stream ->
                    let read = safeReadStream stream buffer
                    if read > 0 then
                        yield (read,buffer)
            }

    ///Construct string messages from a sequence of raw byte array chunks
    ///message delimiter is the newline '\n' character
    let messageScanner rSeq =
        ((0,Array.create 1024 0uy,[]),rSeq) ||> Seq.scan(fun (pR,pBuff,_) (cR,cBuff) ->
            Array.Copy(cBuff,0,pBuff,pR,cR)
            let mutable messages = []
            let lastIndex = pR + cR - 1
            let mutable prevIndex = 0
            for i in 0..lastIndex do
                if pBuff.[i] = 10uy then // 10uy = '\n'
                    let msg = pBuff.[prevIndex..i-1]
                    messages <- msg::messages
                    prevIndex <- i + 1
            let strMsgs = messages |> List.rev |> List.map (fun arr -> ASCIIEncoding.Default.GetString(arr))
            if prevIndex = lastIndex + 1 then //all data is consumed
                (0,pBuff,strMsgs)
            else //data leftover; pass to next cycle
                let indexOfLeftOverData = prevIndex - pR
                let lengthOfLeftOverData = lastIndex - prevIndex + 1
                //copy left over data to state array
                Array.Copy(cBuff,indexOfLeftOverData,pBuff,0,lengthOfLeftOverData)
                (lengthOfLeftOverData,pBuff,strMsgs))
        |> Seq.collect (fun (_,_,s) -> s)
        //let testArray = [4,"wear";10,"to\ntogo981";6,"\n2345\n"] |> Seq.map(fun (a,b) -> a, Encoding.Default.GetBytes(b))
        //let r = messageComposer testArray |> Seq.toArray
 
    ///Returns an agent suitable for handling ReaderAgentMsgs
    ///There should only be one instance of this agent as it reads the connected stream
    let messageProcessorAgent cancelToken = 
        MailboxProcessor.Start(
            (fun inbox ->
            let tag = "messageProcessorAgent"
            let stream = ref (None:System.IO.Stream option)
            let signal = new System.Threading.ManualResetEvent(false)
            let listeners = ref []
            //posts test messages
            (*
            let testPump = 
                async {
                    while true do
                        do! Async.Sleep 1000
                        log tag "posting test message"
                        log tag (sprintf "listener count = %d" listeners.Value.Length)
                        !listeners |> List.iter (fun (_,mp:MailboxProcessor<string>) -> 
                            mp.Post("testing..."))
                    } |> Async.Start
            *)
            let pump = 
                Async.Start (
                    async {
                        rawReadSeq stream signal
                        |> messageScanner
                        |> Seq.iter (fun msg ->
                            !listeners 
                            |> List.iter (fun (id,r:MailboxProcessor<string>) ->
                                if r.CurrentQueueLength < 100 then
                                    try 
                                        r.Post msg 
                                    with ex ->
                                        log tag (sprintf "Error posting msg %s to %s" msg id)
                                        ))
                    },
                    cancelToken)
            async {
                while true do
                    let! msg = inbox.Receive()
                    match msg with
                    | ConnectorStateChanged msg ->
                        match msg with
                        | Connected str ->
                            stream := Some str
                            log tag "signal set"
                            signal.Set() |> ignore       
                        | Connecting 
                        | Disconnected ->
                            log tag "signal reset"
                            signal.Reset() |> ignore
                            stream := None
                    | Register (id,listener) ->
                        log tag (sprintf "registering %s" id)
                        let current = !listeners |> List.tryFind (fun (id2,_) -> id2 = id)
                        listeners :=
                            match current with
                            | None -> ((id,listener):: !listeners) |> List.rev
                            | Some (_,_) ->
                                ([],!listeners) ||> List.fold (fun acc (id2,l) ->
                                if id = id2 then (id,listener)::acc
                                else (id2,l)::acc) |> List.rev
                    | Unregister id ->
                        log tag (sprintf "unregistering %s" id)
                        listeners := ([],!listeners) ||> List.fold (fun acc (id2,l) ->
                            if id = id2 then acc
                            else (id2,l)::acc) |> List.rev
                    | Close r ->
                        signal.Reset() |> ignore
                        stream := None
                        listeners := []
                        r.Reply()
                     | Write bytes ->
                        match !stream with
                        | Some stream -> 
                            try
                                stream.Write(bytes,0,bytes.Length)
                            with ex -> log tag (sprintf "Error writing to stream %s" ex.Message)
                        | None -> log tag "cannot write; no stream connected"}),
            cancelToken)

type VehicleManagerBinder(service:XaVehicleManager) =
    inherit Binder()
    member x.GetService() = service

and [<Service>] XaVehicleManager() =
    inherit Service()
    let tag = "XaVehicleManager"
    let mutable usbConnection = Unchecked.defaultof<UsbConnection>
    let mutable readerAgent = Unchecked.defaultof<MailboxProcessor<ReaderAgentMsg>>
    let cancelTokenSource = new CancellationTokenSource()
    
    override x.OnBind(intent) = new VehicleManagerBinder(x)  :> IBinder     
        
    override x.OnStart(intent, startId) =
        base.OnStart(intent,startId)
        readerAgent <- messaging.messageProcessorAgent(cancelTokenSource.Token)
        let fconn = ConnectorStateChanged>>readerAgent.Post
        usbConnection <- UsbConnection(fconn,x)
        usbConnection.ConnectAsync()
        log tag "started"
       
    override x.OnDestroy() =
        usbConnection.DisconnectAsync()
        readerAgent.PostAndReply((fun rc -> Close(rc)),1000)
        cancelTokenSource.Cancel()
        base.OnDestroy()

    member x.RegisterAsync id receiver = readerAgent.Post(Register(id,receiver))
    member x.UnregisterAsync id = readerAgent.Post(Unregister id)
    member x.WriteAsync bytes = readerAgent.Post(Write bytes)
