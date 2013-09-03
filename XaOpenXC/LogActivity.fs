namespace XaOpenXC

open System
open System.Collections.Generic
open System.Linq
open System.Text

open Android.App
open Android.Content
open Android.OS
open Android.Runtime
open Android.Views
open Android.Widget


[<Activity (Label = "LogActivity")>]
type LogActivity() as this =
    inherit ListActivity()
    let tag = "LogActivity"
    let sc = System.Threading.SynchronizationContext.Current
    let mutable adapter:ArrayAdapter<string> = null
    
    let mutable btnRefresh:Button=null
    let mutable btnClear:Button = null
    
    let setLog() =
        let arr = Logging.getLog()
        adapter <- new ArrayAdapter<string>(this,Android.Resource.Layout.SimpleListItem1,arr)
        this.ListAdapter <- adapter
        ()

    override x.OnCreate(bundle) =
        base.OnCreate (bundle)     
        x.SetContentView (Resource_Layout.Log)
        btnRefresh <- this.FindViewById<Button>(Resource_Id.btnRefresh)
        btnRefresh.Click.Add(fun btn -> setLog())
        btnClear <- this.FindViewById<Button>(Resource_Id.btnClear)
        btnClear.Click.Add(fun btn -> Logging.clearLog(); setLog())    
        setLog()
        


