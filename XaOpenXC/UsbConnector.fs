namespace XaOpenXC
open Android.Hardware.Usb
open Android.Content
open Android.App
open System.IO
open Extensions
open Logging
open XaOpenXC


type UsbLiveConnection = 
    {Conn:UsbDeviceConnection
     Iface:UsbInterface
     InEp:UsbEndpoint
     OutEp:UsbEndpoint option}
     
type private UsbMsg = 
    | UsbConnect
    | UsbPermission of UsbDevice
    | UsbConnected of UsbLiveConnection
    | UsbDisconnect 
    | Error of string
    
exception USBInterfaceNotAvailable

type UsbStream(usb:UsbLiveConnection) =
    inherit Stream()
    let tag = "UsbStream"
    override x.CanRead = true
    override x.CanSeek = false
    override x.CanTimeout = false
    override x.CanWrite = usb.OutEp |> Option.isSome
    override x.Close() = failwith "Cannot close stream. Close UsbConnection object"
    override x.Length = 0L
    override x.Position with get() = 0L and set(v) = ()
    override x.Flush() = ()
    override x.Seek(_,_) = raise (System.NotImplementedException())
    override x.SetLength(_) = ()
  
    override x.Read(buffer,offset,count) =
        if offset = 0 then usb.Conn.BulkTransfer(usb.InEp,buffer,count,0)
        else usb.Conn.BulkTransfer(usb.InEp,buffer.[offset..],count,0)
        
    override x.Write(buffer,offset,count) = 
        match usb.OutEp with
        | None -> ()
        | Some outEp -> 
            let written =
                if offset = 0 then usb.Conn.BulkTransfer(outEp,buffer,count,0)
                else usb.Conn.BulkTransfer(outEp,buffer.[offset..],count,0)
            log tag (sprintf "Written %d" written)
            
module Constants =
    let usb_permission = "XaOpenXC.USB_PERMISSION"
    let vendor_id = 7108
    let product_id =  1
    
type UsbConnection(f:ConnectorState->unit, context:Context) =
    let tag = "UsbConnector"
    let usbManager = context.GetSystemService(Context.UsbService) :?> UsbManager
    let stateChanged st = f st
    let pendingIntent = PendingIntent.GetBroadcast(context,0,new Intent(Constants.usb_permission), PendingIntentFlags.UpdateCurrent)
    
    let isOpenXC (device:UsbDevice) = 
        device.VendorId = Constants.vendor_id && 
        device.ProductId = Constants.product_id
     
    let lookForDevice() = 
        log tag "lookForDevice"
        usbManager.DeviceList
        |> Seq.cast<UsbDevice>
        |> Seq.tryFind (fun d -> 
            log tag (sprintf "Device found %A" d.DeviceName)
            isOpenXC d)
            
    let getEndpoints (device:UsbDevice) =
        log tag "getEndpoints"
        if device.InterfaceCount <> 1 then raise USBInterfaceNotAvailable
        let iface = device.GetInterface(0)
        if iface.EndpointCount <> 2 then raise USBInterfaceNotAvailable
        let ep1 = iface.GetEndpoint(0)
        let ep2 = iface.GetEndpoint(1)
        let inEp,outEp = ((None,None),[ep1;ep2]) ||> List.fold(fun (o1,o2)  ep ->
            match (ep.Type,ep.Direction) with
            | UsbAddressing.XferBulk, UsbAddressing.In  -> Some ep, o2
            | UsbAddressing.XferBulk, UsbAddressing.Out -> o1, Some ep
            | _, _                                      -> o1,o2)
        inEp  |> Option.map(fun e -> e.Type,e.Direction=UsbAddressing.In) |> sprintf "InEndpoint %A"  |> log tag
        outEp |> Option.map(fun e -> e.Type,e.Direction=UsbAddressing.Out) |> sprintf "OutEndpoint %A" |> log tag
        match inEp,outEp with
        | None,_ -> raise USBInterfaceNotAvailable
        | _,_    -> iface,inEp.Value,outEp
          
    let connectDevice(device:UsbDevice) =
        log tag "connectDevice"
        let iface,inEp,outEp = getEndpoints(device)
        let conn = usbManager.OpenDevice(device)
        log tag (sprintf "conn=%A" conn)
        if conn = null then raise USBInterfaceNotAvailable
        conn.ClaimInterface(iface,true) |> ignore
        {Conn=conn; Iface=iface; InEp=inEp; OutEp=outEp}

    let requestConnection (mb:MailboxProcessor<UsbMsg>) =
        log tag "requestConnection"
        let connectDevice device =
            let usb = connectDevice device
            mb.Post(UsbConnected usb)
        let registerConnect() =
            let intentFilter1 = new IntentFilter(UsbManager.ActionUsbDeviceAttached)
            let intentFilter2 = new IntentFilter(Constants.usb_permission)
            let filterHandler (intent:Intent) =
                try
                    log tag "received connect intent"
                    let device = intent.GetParcelableExtra(UsbManager.ExtraDevice) :?> UsbDevice
                    if device = null then raise USBInterfaceNotAvailable
                    if isOpenXC device then
                        let permission = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false)
                        log tag (sprintf "usb permission=%A" permission)
                        if not permission then mb.Post(UsbPermission device)
                        else connectDevice device
                with
                | ex -> mb.Post(Error ex.Message)
            context.RegisterReceiver(receiveBroadcast filterHandler, intentFilter1) |> ignore
            context.RegisterReceiver(receiveBroadcast filterHandler, intentFilter2) |> ignore
        let registerDisconnect() =            
            let intentFilter = new IntentFilter(UsbManager.ActionUsbDeviceDetached)
            let filterHandler (intent:Intent) =
                log tag "received disconnect intent"
                let device = intent.GetParcelableExtra(UsbManager.ExtraDevice) :?> UsbDevice
                if device = null then raise USBInterfaceNotAvailable
                if isOpenXC device then
                    mb.Post UsbDisconnect
            context.RegisterReceiver(receiveBroadcast filterHandler, intentFilter) |> ignore
        let attempToConnectToAttachedDevice() = //try to connect if usb device is already attached
            match lookForDevice() with
            | Some device -> 
                log tag "connected device found"
                if usbManager.HasPermission(device) then
                    try connectDevice device with ex -> mb.Post(Error ex.Message)
                else mb.Post(UsbPermission device)
            | _ -> ()
        registerDisconnect()
        registerConnect()
        attempToConnectToAttachedDevice()
             
    let usbMsgString = function 
    | UsbConnected (_)->"UsbConnected" | UsbConnect->"UsbConnect" | UsbPermission _->"UsbPermission"
    | UsbDisconnect->"UsbDisconnect"   | Error ex -> "Error: " + ex
    
    let connectorAgent = MailboxProcessor.Start <| fun inbox ->
        let state = ref Disconnected
        let connection = ref (None:UsbLiveConnection option)
        let closeConnectionIfOpen() =
            match !connection with
            | Some usb -> 
                usb.Conn.ReleaseInterface(usb.Iface) |> ignore
                usb.Conn.Close()
            | None -> ()       
        async {
            while true do
            try
                let! msg = inbox.Receive()
                log tag (usbMsgString msg)
                match msg with
                | UsbConnect ->
                    closeConnectionIfOpen()
                    state := Connecting
                    stateChanged !state
                    requestConnection inbox
                | UsbConnected usb ->
                    closeConnectionIfOpen()
                    connection := Some usb
                    let str = new UsbStream(usb)
                    log tag "priming"
                    let prime = System.Text.UnicodeEncoding.Default.GetBytes("prime\u0000")
                    str.Write(prime,0,prime.Length)
                    state := Connected(str)
                    stateChanged !state
                | UsbDisconnect ->
                    closeConnectionIfOpen()
                    state := Disconnected
                    stateChanged !state
                | UsbPermission device ->
                    closeConnectionIfOpen()
                    usbManager.RequestPermission(device,pendingIntent)
                | Error err -> 
                    log tag err
                    inbox.Post UsbDisconnect
            with _ ->  ()
        }
        
    member x.ConnectAsync() = connectorAgent.Post(UsbConnect)
    member x.DisconnectAsync() = connectorAgent.Post(UsbDisconnect)
        
    
