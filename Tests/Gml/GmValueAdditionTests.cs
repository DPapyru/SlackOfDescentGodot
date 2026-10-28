using SlackOfDescent;

public static class GmValueAdditionTests
{
    public static int Run()
    {
        int count=0;
        void Check(GmValue actual,GmValue expected,string name){if(!actual.Equals(expected))throw new Exception($"{name}: expected {expected}, got {actual}");count++;}
        Check(new GmValue("Cost  :  ")+new GmValue(100),new GmValue("Cost  :  100"),"string plus integer appends numeric text");
        Check(new GmValue(100)+new GmValue("  :  Cost"),new GmValue("100  :  Cost"),"integer plus string prepends numeric text");
        Check(new GmValue("Slack ")+new GmValue("Of Descent"),new GmValue("Slack Of Descent"),"string plus string concatenates");
        Check(new GmValue(2)+new GmValue(3),new GmValue(5),"real plus real stays arithmetic");
        Check(new GmValue(0.5)+new GmValue(1),new GmValue(1.5),"fractional real plus real stays arithmetic");
        Check(new GmValue("v")+new GmValue(1.5),new GmValue("v1.5"),"fractional real uses shortest exact text");
        Check(new GmValue(1.5)+new GmValue("v"),new GmValue("1.5v"),"fractional real concatenates in either order");
        Check(new GmValue("n")+new GmValue(-0.25),new GmValue("n-0.25"),"negative real keeps its sign in text");
        Check(new GmValue("")+new GmValue(7),new GmValue("7"),"empty string still forces text result");
        Check(new GmValue("x")+new GmValue(1000000),new GmValue("x1000000"),"large integer omits exponent notation");
        Check(new GmValue("x")+(new GmValue(1)/new GmValue(3)),new GmValue("x0.333333333333333"),"repeating fraction truncates at fixed precision");
        Check(new GmValue("a")+new GmValue(1)+new GmValue(2),new GmValue("a12"),"chained addition keeps text once captured");
        Check(new GmValue(1)+new GmValue(2)+new GmValue("a"),new GmValue("3a"),"numeric pair evaluates before concatenation");
        return count;
    }
}
