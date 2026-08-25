namespace FactorioLibrary.Models
{
    public class ServerStats
    {
        public double CpuPercentage { get; set; }
        public double RamUsageMb { get; set; }
        public double RamLimitMb { get; set; }
        public List<string> OnlinePlayers { get; set; } = [];
        public bool IsOnline { get; set; }
        public int OnlineCpus { get; set; }
    }
}
