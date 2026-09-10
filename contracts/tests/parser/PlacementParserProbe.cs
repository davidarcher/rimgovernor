using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
// Offline compatibility probe; no game assemblies, SDK calls or game state.
// Validate below is copied verbatim from PlacementPreviewsTool.Preview's parser block.
// The fixture records both source hashes; this probe is not a replacement runtime parser.
class Program {
 static object Validate(string placements) {
            JArray candidates;
            try
            {
                if (placements == null || placements.Length > 32768) throw new ArgumentException("Missing or oversized placements");
                candidates = JArray.Parse(placements, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (candidates.Count < 1 || candidates.Count > 16) throw new ArgumentException("Use 1..16 placements");
                foreach (var item in candidates)
                {
                    var row = item as JObject;
                    if (row == null || row.Properties().Count() != 5
                        || row.Properties().Any(p => !new[] { "defName", "x", "z", "rotation", "stuff" }.Contains(p.Name)))
                        throw new ArgumentException("Use exactly defName, x, z, rotation and stuff");
                    foreach (var key in new[] { "defName", "rotation", "stuff" })
                        if (row[key].Type != JTokenType.String || ((string)row[key]).Length > 200
                            || key != "stuff" && string.IsNullOrWhiteSpace((string)row[key]))
                            throw new ArgumentException("Invalid definition, rotation or material");
                    foreach (var key in new[] { "x", "z" })
                        if (row[key].Type != JTokenType.Integer || !int.TryParse(row[key].ToString(), out _))
                            throw new ArgumentException("Coordinates must be 32-bit integers");
                }
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException)
            { return new { success = false, error = "Invalid placement batch: " + error.Message }; }

 return new {success=true};
 }
 static string Expand(JToken input) {
 if(input==null || input.Type==JTokenType.Null) return null;
 if(input.Type==JTokenType.String) return (string)input;
 string json=(string)input["json"];
 foreach(JObject replacement in input["replacements"] as JArray ?? new JArray())
  json=json.Replace((string)replacement["token"], string.Concat(Enumerable.Repeat((string)replacement["text"],(int)replacement["count"])));
 return json;
 }
 static void Main(string[] args) {
 var rows=new List<object>();
 JToken fixture=JToken.Parse(File.ReadAllText(args[0]));
 foreach(JObject c in fixture as JArray ?? (JArray)fixture["cases"]) {
 string input=Expand(c["input"]);
 var result=new Dictionary<string,object>{{"id",(string)c["id"]},{"input_utf16_units", input==null ? (object)null : input.Length}};
 try {
 var parsed=JArray.Parse(input,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error });
 result["parse_accepted"]=true;
 var obj=parsed.First as JObject;
 if(obj!=null) {
 result["x_token_type"]=obj["x"]==null ? null : obj["x"].Type.ToString();
 result["x_display"]=obj["x"]==null ? null : obj["x"].ToString();
 if(obj["defName"]!=null && obj["defName"].Type==JTokenType.String) {
 string s=(string)obj["defName"];result["def_name_utf16_units"]=s.Length;
 result["def_name_utf16_hex"]=string.Join(" ",s.Select(ch=>((int)ch).ToString("X4")));
 result["def_name_is_whitespace"]=string.IsNullOrWhiteSpace(s);
 }
 } 
 } catch(Exception e) {result["parse_accepted"]=false; result["parse_error_type"]=e.GetType().FullName;result["parse_error"]=e.Message;}
 result["native_structural_validation"]=Validate(input);
 rows.Add(result);
 }
 Console.WriteLine(JsonConvert.SerializeObject(new {runtime=Environment.Version.ToString(),framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,assembly=typeof(JArray).Assembly.FullName,assembly_sha256=BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(File.ReadAllBytes(typeof(JArray).Assembly.Location))).Replace("-", "").ToLowerInvariant(),cases=rows},Formatting.Indented));
 }
}
