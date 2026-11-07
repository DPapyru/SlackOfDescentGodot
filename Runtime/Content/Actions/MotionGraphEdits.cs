namespace SlackOfDescent;

public static class MotionGraphEdits
{
    public static void RenameState(MotionGraph graph, string from, string to)
    {
        var state = graph.States.FirstOrDefault(item => item.Id == from) ?? throw new InvalidDataException("状态不存在：" + from);
        if (string.IsNullOrWhiteSpace(to)) throw new InvalidDataException("状态标识不能为空。");
        if (to == from) return;
        if (graph.States.Any(item => item.Id == to)) throw new InvalidDataException("状态标识已存在：" + to);
        state.Id = to;
        if (graph.Entry == from) graph.Entry = to;
        foreach (var item in graph.States) foreach (var transition in item.Transitions) if (transition.Target == from) transition.Target = to;
        graph.Validate();
    }

    public static void RemoveState(MotionGraph graph, string id)
    {
        if (graph.States.Count <= 1) throw new InvalidDataException("至少保留一个状态。");
        var state = graph.States.FirstOrDefault(item => item.Id == id) ?? throw new InvalidDataException("状态不存在：" + id);
        graph.States.Remove(state);
        foreach (var item in graph.States) item.Transitions.RemoveAll(transition => transition.Target == id);
        if (graph.Entry == id) graph.Entry = graph.States[0].Id;
        graph.Validate();
    }
}
