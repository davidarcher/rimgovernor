#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HomeBridge.BridgeTools {
 // Names annotate the existing binary hash input; they never change its bytes.
 internal sealed class NativeBillConfiguration {
  private readonly BinaryWriter writer;
  private readonly Dictionary<string,string>? fields;
  internal NativeBillConfiguration(BinaryWriter writer,Dictionary<string,string>? fields){this.writer=writer;this.fields=fields;}
  internal void Write(string name,object value){
   string text;
   switch(value){
    case bool b: writer.Write(b);text=b?"true":"false";break;
    case int i: writer.Write(i);text=i.ToString(CultureInfo.InvariantCulture);break;
    case float f: writer.Write(f);text=f.ToString("R",CultureInfo.InvariantCulture);break;
    case string s: writer.Write(s);text=s;break;
    default: throw new InvalidOperationException("Unsupported bill configuration value");
   }
   if(fields!=null)fields.Add(name,Bound(text));
  }
  internal void Group(string name,Action<BinaryWriter> write){
   if(fields==null){write(writer);return;}
   using(var bytes=new MemoryStream()){
    using(var nested=new BinaryWriter(bytes,Encoding.UTF8,true))write(nested);
    var data=bytes.ToArray();writer.Write(data);fields.Add(name,Digest(data));
   }
  }
  private static string Digest(byte[] bytes){using(var hash=SHA256.Create())return "sha256:"+BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
  private static string Bound(string text)=>text.Length<=80&&text.All(c=>c>=32&&c<=126&&c!=';'&&c!='=')?text:Digest(Encoding.UTF8.GetBytes(text));
  // At most six changes, 80 ASCII bytes per value, and 1536 bytes overall.
  // Full collection digests retain changes outside a displayed prefix.
  internal static string Changes(Dictionary<string,string> before,Dictionary<string,string> after,int beforeIndex,int afterIndex){
   var changes=new List<string>();
   if(beforeIndex!=afterIndex)changes.Add("index="+beforeIndex.ToString(CultureInfo.InvariantCulture)+" -> "+afterIndex.ToString(CultureInfo.InvariantCulture));
   foreach(var key in before.Keys.Union(after.Keys).OrderBy(k=>k,StringComparer.Ordinal)){
    var a=before.TryGetValue(key,out var old)?old:"<absent>";
    var b=after.TryGetValue(key,out var current)?current:"<absent>";
    if(a!=b)changes.Add(key+"="+a+" -> "+b);
   }
   var result="Original bill configuration changed. "+string.Join("; ",changes.Take(6));
   if(changes.Count>6)result+="; omitted="+(changes.Count-6).ToString(CultureInfo.InvariantCulture);
   return result.Length<=1536?result:result.Substring(0,1516)+"; detail truncated";
  }
 }
}
