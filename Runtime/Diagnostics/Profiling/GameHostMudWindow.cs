using Godot;
using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public partial class GameHost
{
    private GmInstance? mudWindowPlayer;
    partial void PositionMudWindow()
    {
        if(mudWindowPlayer is not { } player)return;
        world.Write(player,"x",2478);world.Write(player,"y",1628);
        world.Write(player,"hspeed",0);world.Write(player,"vspeed",0);
    }
}
