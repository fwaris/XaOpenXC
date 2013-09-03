namespace XaOpenXC
open Android.Content
open System
open Android.App
open Android.Content
open Android.OS
open Android.Runtime

module Extensions =         
    type BR(f:Intent->unit) =
        inherit BroadcastReceiver()
        override x.OnReceive(ctx:Context,intent:Intent) = f intent
        
    let receiveBroadcast f = new BR(f)

      
    type SC<'t when 't:>IBinder>(onConnected:ComponentName*'t ->unit, onDisconnected:ComponentName->unit) =
        inherit Java.Lang.Object()
        interface IServiceConnection with
            member x.OnServiceConnected(name,binder) = onConnected(name,binder :?> 't)
            member x.OnServiceDisconnected(name) = onDisconnected(name)
            
    let serviceConnection<'t when 't :> IBinder> f1 f2 = new SC<'t>(f1,f2)
 