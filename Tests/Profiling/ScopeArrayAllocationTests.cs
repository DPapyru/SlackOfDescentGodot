using System.Text.Json;
using SlackOfDescent;

public static class ScopeArrayAllocationTests
{
    public static int Run()
    {
        int count=0;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);count++;}
        void Reject(Action action,string name)
        {
            bool rejected=false;
            try{action();}catch(IndexOutOfRangeException){rejected=true;}
            Check(rejected,name);
        }
        var scope=new GmScope();
        scope.Write("moss_m",3);
        scope.Write("moss_m[3]",7);
        scope.Write("moss_m",9,5);
        Check(scope.Read("moss_m").Equals(3),"array migration preserves scalar alias");
        Check(scope.Read("moss_m",0).Equals(3),"array index zero aliases scalar");
        Check(scope.Read("moss_m",3).Equals(7),"array migration preserves flattened entries");
        Check(scope.Read("moss_m",2).Equals(0),"array gaps read as numeric zero");
        Check(!scope.Values.ContainsKey("moss_m[2]"),"array gaps remain absent from reflective values");
        Check(scope.ReadOr("moss_m[2]",17).Equals(17),"array gap ReadOr retains fallback");
        Check(scope.ReadOr("missing",19).Equals(19),"missing variable ReadOr retains fallback");
        Check(scope.Values.Count==3,"array migration retains logical entry count");
        scope.Write("moss_m[3]",11);
        Check(scope.Read("moss_m",3).Equals(11),"flattened writes reach migrated arrays");
        scope.Write("moss_m",13,3);
        Check(scope.Read("moss_m[3]").Equals(13),"indexed writes reach flattened reads");
        Check(scope.Values.TryGetValue("moss_m[3]",out var reflected)&&reflected.Equals(13),"array Values lookup sees indexed writes");
        scope.Write("moss_m",23);
        Check(scope.Read("moss_m",0,0).Equals(23),"scalar writes update migrated zero entry");
        scope.Write("moss_m",29,2,4);
        Check(scope.Read("moss_m[2,4]").Equals(29),"two dimensional entries expose canonical keys");
        Check(scope.Read("moss_m",2,1).Equals(0),"two dimensional rows retain implicit zeros");
        Check(!scope.Values.ContainsKey("moss_m[2,1]"),"two dimensional gaps remain undefined reflectively");
        bool missing=false;try{scope.Read("moss_m",2,5);}catch(KeyNotFoundException){missing=true;}
        Check(missing,"array reads beyond written length remain uninitialized");
        missing=false;try{scope.Read("moss_m",1,0);}catch(KeyNotFoundException){missing=true;}
        Check(missing,"missing array rows remain uninitialized");
        Reject(()=>scope.Read("moss_m",-1),"array reads reject negative indices");
        Reject(()=>scope.Write("moss_m",1,32000),"array writes reject upper limit");
        Reject(()=>scope.Write("moss_m",1,-1,0),"array writes reject negative row");
        scope.Write("moss_m[3]",31,2);
        Check(scope.Read("moss_m[3]",2).Equals(31),"literal bracket names retain nested key behavior");
        Check(scope.Read("moss_m[3]").Equals(13),"literal bracket arrays retain existing scalar alias");
        foreach(var (name,indices) in new (string,GmIndices)[]{("moss_m",default),("moss_m",new(0)),("moss_m",new(2)),("moss_m",new(3)),("moss_m",new(2,1)),("moss_m",new(2,4)),("moss_m",new(2,5)),("moss_m[3]",default),("moss_m[3]",new(2)),("missing",new(3))})
            Check(scope.Contains(name,indices)==scope.Values.ContainsKey(GmScope.Key(name,indices)),"structured definition query matches reflection: "+name);
        Reject(()=>scope.Contains("moss_m",new(-1)),"structured definition query rejects negative indices");
        var snapshot=scope.Values.ToDictionary(p=>p.Key,p=>p.Value);
        Check(snapshot.Count==scope.Values.Count,"reflective enumeration retains logical entry count");
        Check(snapshot["moss_m[2,4]"].Equals(29)&&snapshot["moss_m[3][2]"].Equals(31),"reflective enumeration preserves canonical keys");
        string json=JsonSerializer.Serialize(scope.Values.ToDictionary(p=>p.Key,p=>p.Value.ToString()));
        var restored=new GmScope();
        foreach(var entry in JsonSerializer.Deserialize<Dictionary<string,string>>(json)!)restored.Write(entry.Key,double.Parse(entry.Value,System.Globalization.CultureInfo.InvariantCulture));
        foreach(var entry in snapshot)Check(restored.Read(entry.Key).Equals(entry.Value),"serialized array entry round trips: "+entry.Key);
        int changed=0;scope.Changed=_=>changed++;
        scope.Write("moss_m",13,3);scope.Write("moss_m",41,3);scope.Write("moss_m",41,3);
        Check(changed==1,"array writes notify only when defined values change");
        var hot=new GmScope();
        for(int index=0;index<64;index++){hot.Write("moss_m",index,index);hot.Write("moss_mt",index,2,index);}
        for(int iteration=0;iteration<4096;iteration++)Update(iteration);
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int iteration=0;iteration<4096;iteration++)Update(iteration);
        allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        Check(allocated<1024,"steady array reads and writes avoid formatted key allocations");
        return count;

        void Update(int iteration)
        {
            int index=iteration%64;
            hot.Write("moss_m",hot.Read("moss_m",index)+1,index);
            hot.Write("moss_mt",hot.Read("moss_mt",2,index)+1,2,index);
        }
    }
}
