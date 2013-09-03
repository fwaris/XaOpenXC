namespace XaOpenXC
open System
open System.Text
open Android.App
open Android.Content
open Android.OS
open Android.Runtime
open Android.Views
open Android.Widget
open XaOpenXC
open Logging

[<Activity (Label = "XaOpenXC", MainLauncher = true)>]
type MainActivity () =
    inherit Activity ()
    let tag = "MainActivity"
    
    let uiThreadContext = System.Threading.SynchronizationContext.Current   
    let mutable t1:TextView = null
    
    let setTextOnView t (view:TextView) = 
        async {
            do! Async.SwitchToContext uiThreadContext
            view.Text <- t      
        } |> Async.Start
        
    let setText t = 
        setTextOnView t t1
             
    override this.OnCreate (bundle) =
        base.OnCreate (bundle)
        this.SetContentView (Resource_Layout.Main)
        t1 <- this.FindViewById<TextView>(Resource_Id.textView1)
        let btnStartService = this.FindViewById<Button>(Resource_Id.btnStartService)
        let btnStopService = this.FindViewById<Button>(Resource_Id.btnStopService)
        let btnViewMsgs = this.FindViewById<Button>(Resource_Id.btnViewMessages)
        let btnViewLog = this.FindViewById<Button>(Resource_Id.btnViewLog)
        
        btnStartService.Click.Add(fun _ ->
            new Intent(this,typeof<XaVehicleManager>)
            |> this.StartService
            |> ignore
            )
        btnStopService.Click.Add(fun _ ->
            new Intent(this,typeof<XaVehicleManager>)
            |> this.StopService
            |> ignore
            )
        btnViewMsgs.Click.Add(fun _ ->
            new Intent(this,typeof<MessagesActivity>)
            |> this.StartActivity
            |> ignore
            )
        btnViewLog.Click.Add(fun _ ->
            new Intent(this,typeof<LogActivity>)
            |> this.StartActivity
            |> ignore
            )

    override this.OnDestroy() =
        log tag "onDestory"
        base.OnDestroy()
 
