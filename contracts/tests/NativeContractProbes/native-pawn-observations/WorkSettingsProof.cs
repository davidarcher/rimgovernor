#nullable enable
using System;
using System.Reflection;
using System.Linq;

internal static class WorkSettingsProof
{
    internal static void Run(Assembly bridge, Action<bool,string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var type = bridge.GetType("HomeBridge.BridgeTools.NativeWorkSettings", true)!;
        Func<string,string,object> parse = (name,json) => {
            var parser = bridge.GetType("RimGovernor.Protocol." + name,true)!.GetProperty("Parser")!.GetValue(null)!;
            return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;
        };
        Func<string,bool> valid = json => (bool)type.GetMethod("Valid",flags)!.Invoke(null,new[]{parse("Operations.PatchPawn",json)})!;
        const string pawn = "\"pawn\":{\"entityId\":\"Pawn1\",\"expectedSnapshotToken\":\"token\"}";
        const string work = "\"work\":[{\"workTypeDef\":\"Cooking\",\"priority\":1}]";
        check(valid("{"+pawn+","+work+"}"),"work-only patch accepted");
        foreach(var extra in new[]{"\"schedule\":{}","\"medicalCare\":1","\"selfTend\":false","\"followDrafted\":false","\"followFieldwork\":false","\"slaughter\":false","\"releaseToWild\":false","\"allowedArea\":{}","\"master\":{}"})
            check(!valid("{"+pawn+","+work+","+extra+"}"),"work cannot widen to "+extra);
        foreach(var invalidRows in new[]{"[]","[{\"workTypeDef\":\"Cooking\"}]","[{\"workTypeDef\":\"Cooking\",\"priority\":-1}]","[{\"workTypeDef\":\"Cooking\",\"priority\":5}]","[{\"workTypeDef\":\"Cooking\",\"priority\":1},{\"workTypeDef\":\"Cooking\",\"priority\":2}]"})
            check(!valid("{"+pawn+",\"work\":"+invalidRows+"}"),"invalid work priorities rejected");
        var identity=parse("Common.Identity","{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}");
        Func<int,bool,Array> rows=(priority,disabled)=> {
            var row=parse("Observations.WorkSetting","{\"defName\":\"Cooking\",\"priority\":"+priority+",\"disabled\":"+(disabled?"true":"false")+"}");
            var array=Array.CreateInstance(row.GetType(),1);array.SetValue(row,0);return array;
        };
        var schedule=Enumerable.Repeat("Anything",24).ToArray();
        var changedSchedule=(string[])schedule.Clone();changedSchedule[12]="Work";
        object[] inputs={identity,"Pawn1",true,rows(1,false),"Area1",schedule};
        Func<object[],string> token=values=>(string)type.GetMethod("Token",flags)!.Invoke(null,values)!;
        var original=token(inputs);check(original==token(inputs),"stable work snapshot");
        foreach(var change in new object[][]{
            new object[]{identity,"Pawn2",true,rows(1,false),"Area1",schedule},new object[]{identity,"Pawn1",false,rows(1,false),"Area1",schedule},
            new object[]{identity,"Pawn1",true,rows(2,false),"Area1",schedule},new object[]{identity,"Pawn1",true,rows(1,true),"Area1",schedule},
            new object[]{parse("Common.Identity","{\"colonyId\":\"colony\",\"loadToken\":\"other\",\"mapId\":0}"),"Pawn1",true,rows(1,false),"Area1",schedule},
            new object[]{identity,"Pawn1",true,rows(1,false),"Area2",schedule},
            new object[]{identity,"Pawn1",true,rows(1,false),"",schedule},
            new object[]{identity,"Pawn1",true,rows(1,false),"Area1",changedSchedule},
            new object[]{identity,"Pawn1",true,rows(1,false),"Area1",Array.Empty<string>()}
        }) check(original!=token(change),"work snapshot binds pawn, mode, priorities, capability, world, area and schedule");
    }
}
