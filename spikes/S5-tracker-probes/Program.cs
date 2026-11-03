static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "p1": return P1.Run();
            case "p1c": return P1c.Run();
            case "p3": return P3.Run();
            case "p2": return P2.Run();
            case "p5": return P5.Run();
            case "p6": return P6.Run();
            case "churn": return P5.Churn(int.Parse(args[1]));
            case "p2server": return P2.Server();
            default: Console.WriteLine("usage: S5Probes p1 | p2 | p3 | p5"); return 2;
        }
    }
}
