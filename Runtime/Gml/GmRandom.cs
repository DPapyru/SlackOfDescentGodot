using System.Security.Cryptography;

namespace SlackOfDescent;

public sealed class GmRandom
{
    public int Seed { get; set; }
    public GmRandom(){Randomize();}
    public GmRandom(int seed){Seed=seed;}
    public void Randomize()=>Seed=BitConverter.ToInt32(RandomNumberGenerator.GetBytes(4));
    private uint Cycle(){Seed=unchecked(Seed*0x08088405+1);return unchecked((uint)Seed);}
    public double Next(double bound)=>Cycle()*(1.0/4294967296.0)*bound;
    public int NextInt(uint bound)=>unchecked((int)(((ulong)Cycle()*unchecked(bound+1))>>32));
}
