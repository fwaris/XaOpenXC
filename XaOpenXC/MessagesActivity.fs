namespace XaOpenXC

open System
open System.Collections.Generic
open System.Linq
open System.Text
open System.Threading
open Logging
open Extensions

open Android.App
open Android.Content
open Android.OS
open Android.Runtime
open Android.Views
open Android.Widget

[<Activity (Label = "Messages")>]
type MessagesActivity() =
    inherit ListActivity()
    let tag = "MessagesActivity"
    
    let uiThreadContext = System.Threading.SynchronizationContext.Current
    let cancelTokenSource = new CancellationTokenSource()
    let mutable uiUpdateAgentInstance = Unchecked.defaultof<MailboxProcessor<string>>
    let mutable vehicleManager = None
    
    //updates the running message list on screen
    let uiUpdateAgent (adapter:ArrayAdapter<string>) cancelToken = 
        MailboxProcessor.Start (
            (fun inbox ->
                async{
                    while true do
                       try
                            let! (msg:string) = inbox.Receive()
                            log tag (sprintf "received msg %s" msg)
                            do! Async.SwitchToContext uiThreadContext
                            //NOTE: Update the ArrayAdapter on the UI thread only
                            adapter.Add(msg)
                            if adapter.Count > 30 then 
                                adapter.Remove(adapter.GetItem(0))
                            adapter.NotifyDataSetChanged()
                            do! Async.SwitchToThreadPool()
                        with ex -> log tag ex.Message
                }),
            cancelToken)
    
    let checkServiceStarted() =
            async {
                do! Async.Sleep 2000
                match vehicleManager with
                | Some _ -> ()
                | None -> uiUpdateAgentInstance.Post("Service not started (waited 2 seconds)")
            } |> Async.Start
            
    let serviceConnection =
        serviceConnection<VehicleManagerBinder>
            (fun (n,b)-> 
                let service = b.GetService()
                vehicleManager <- Some service
                service.RegisterAsync tag uiUpdateAgentInstance)
            (fun n -> ())
            
    override x.OnCreate(bundle) =
        base.OnCreate (bundle)
        x.SetContentView (Resource_Layout.Messages)
        let adapter = new ArrayAdapter<string>(x,Android.Resource.Layout.SimpleListItem1)
        adapter.SetNotifyOnChange(false)
        x.ListAdapter <- adapter
        uiUpdateAgentInstance <- uiUpdateAgent adapter cancelTokenSource.Token
        let intent = new Intent(x,typeof<XaVehicleManager>)
        x.BindService(intent,serviceConnection,Bind.None) |> ignore
        checkServiceStarted()
        
    override x.OnDestroy()=
        cancelTokenSource.Cancel()
        match vehicleManager with
        | Some vm -> 
            vm.UnregisterAsync(tag)
            x.UnbindService (serviceConnection)
        | None -> ()
        base.OnDestroy()
        


