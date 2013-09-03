namespace XaOpenXC
open System
open System.IO
open System.Text
open System.Threading
open Logging

type ConnectorState = Connected of Stream | Disconnected | Connecting
