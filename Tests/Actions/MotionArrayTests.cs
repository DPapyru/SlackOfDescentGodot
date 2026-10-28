using SlackOfDescent;

public static class MotionArrayTests
{
    public static int Run(GmWorld world,int objectId)
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);count++;}
        void Same(GmScope actual,GmScope expected,string label)
        {
            var snapshot=actual.Values.ToDictionary(p=>p.Key,p=>p.Value);
            Check(snapshot.Count==expected.Values.Count,label+" logical entry count");
            foreach(var entry in expected.Values)
            {
                Check(actual.Values.TryGetValue(entry.Key,out var value)&&value.Equals(entry.Value),label+" reflective entry "+entry.Key);
                Check(actual.Read(entry.Key).Equals(entry.Value),label+" flattened read "+entry.Key);
            }
        }
        var sparse=new GmScope();var eager=new GmScope();
        void Write(int row,int column,GmValue value){sparse.Write("array",value,[row,column]);eager.Write("array",value,[row,column]);}
        void Fill(int row,int length,GmValue value)
        {
            sparse.FillArrayPrefix("array",row,length,value);
            for(int column=0;column<length;column++)eager.Write("array",value,[row,column]);
            Same(sparse,eager,"array prefix "+row+"/"+length);
        }
        Write(2,8,9);Write(0,0,"scalar");Fill(2,6,1);Fill(0,5,-1);
        Check(sparse.Read("array",[2,7]).Equals(0),"unwritten gap remains numeric zero");
        Check(!sparse.Values.ContainsKey("array[2,7]"),"implicit gap is absent from reflective dictionary");
        Check(sparse.ReadOr("array").Equals(-1),"row zero aliases the scalar variable");
        Write(2,2,17);Write(2,5,"tail");Fill(2,3,0);Fill(2,9,1);Fill(2,2,"prefix");
        Write(2,1,"override");Write(2,1,"prefix");Same(sparse,eager,"override restored to default");
        sparse.Write("array[2,1]",7);eager.Write("array[2,1]",7);
        Check(sparse.Read("array",[2,1]).Equals(7),"flattened writes update indexed sparse entries");
        Same(sparse,eager,"flattened sparse write");
        sparse.FillArrayPrefix("source",0,5,1);sparse.Write("source",45,[1]);
        sparse.CopyArrayPrefix("source","array",2,5);
        for(int column=0;column<5;column++)eager.Write("array",column==1?45:1,[2,column]);
        Same(sparse,eagerWithSource(),"sparse prefix copy preserves tail");
        sparse.FillArrayPrefix("source",0,5,1);sparse.CopyArrayPrefix("source","array",2,5);
        for(int column=0;column<5;column++)eager.Write("array",1,[2,column]);
        Same(sparse,eagerWithSource(),"copy clears stale overrides");
        sparse.CopyArrayPrefix("source","source",0,5);
        Check(sparse.Read("source",[4]).Equals(1),"self copy preserves default row");
        int changed=0;sparse.Changed=_=>changed++;
        sparse.Write("array",17,[2,1]);sparse.Write("array",17,[2,1]);sparse.Write("array",1,[2,1]);
        Check(changed==2,"sparse writes notify actual changes and suppress equal writes");
        bool rejected=false;try{sparse.Read("array",[-1]);}catch(IndexOutOfRangeException){rejected=true;}
        Check(rejected,"sparse reads reject negative array index");
        rejected=false;try{sparse.Write("array",1,[32000]);}catch(IndexOutOfRangeException){rejected=true;}
        Check(rejected,"sparse writes retain GM upper index limit");

        var original=world.Create(objectId,0,0,run:false);var native=world.Create(objectId,0,0,run:false);
        var originalPart=world.Create(objectId,0,0,run:false);var nativePart=world.Create(objectId,0,0,run:false);
        original.Scope.Write("Process",originalPart.Id);native.Scope.Write("Process",nativePart.Id);
        foreach(GmValue[] args in new GmValue[][]{[3,4,1,2,3],[2,2,1,1,1],[0,0,0,0,0],[2.25,3.25,1,1.25,2.25],[3,4,1,2,3]})
        {
            world.Evaluator.ResetBudget();world.Evaluator.Run(world.Scripts["MotionInitialize"],new(original,args:args));
            world.Call("MotionInitialize",args,new(native));
            CompareActor("initialize");
        }
        const string path="Chara/Motion/SparseRuntimeTest.txt";
        foreach(string code in new[]{"data_motLong=2;data_motStep[0]=3;with(Process[0]){data_motPartAngle[1]=45;data_motPartScaleX[3]=2;}","data_motLong=1;", "KEYFRM_MAX=2;PARTS_MAX=0;data_motLong=1;"})
        {
            world.External[path]=world.Compile(code);GmValue[] args=[2,"SparseRuntimeTest"];
            world.Evaluator.ResetBudget();world.Evaluator.Run(world.Scripts["setMotionForPlayer"],new(original,args:args));
            world.Call("setMotionForPlayer",args,new(native));CompareActor("motion reload");
            Check(world.Call("variable_local_array2_get",["motPartScaleX",2,3],new(nativePart)).Equals(originalPart.Scope.Read("motPartScaleX",[2,3])),"reflective GM getter sees sparse scale default or override");
        }
        return count;

        GmScope eagerWithSource()
        {
            for(int column=0;column<5;column++)eager.Write("source",sparse.Read("source",[column]),[column]);
            return eager;
        }
        void CompareActor(string label)
        {
            Check(native.Scope.Values.Count==original.Scope.Values.Count,label+" actor dictionary count");
            foreach(var entry in original.Scope.Values.Where(p=>p.Key!="Process"))
                Check(native.Scope.Read(entry.Key).Equals(entry.Value),label+" actor "+entry.Key);
            Same(nativePart.Scope,originalPart.Scope,label+" part");
        }
    }
}
