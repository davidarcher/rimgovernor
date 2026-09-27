#nullable enable
using System;
using System.IO;

namespace HomeBridge.BridgeTools {
 // Names annotate the binary hash input; they never change its bytes.
 internal sealed class NativeBillConfiguration {
  private readonly BinaryWriter writer;
  internal NativeBillConfiguration(BinaryWriter writer){this.writer=writer;}
  internal void Write(string name,object value){
   switch(value){
    case bool b: writer.Write(b);break;
    case int i: writer.Write(i);break;
    case float f: writer.Write(f);break;
    case string s: writer.Write(s);break;
    default: throw new InvalidOperationException("Unsupported bill configuration value "+name);
   }
  }
  internal void Group(string name,Action<BinaryWriter> write)=>write(writer);
 }
}
