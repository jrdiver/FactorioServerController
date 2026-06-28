using System.Collections.Generic;

namespace FactorioLibrary.Models
{
    public class ServerStats
    {
        public double CpuPercentage { get; set; }
        public double RamUsageMb { get; set; }
        public double RamLimitMb { get; set; }
        public List<string> OnlinePlayers { get; set; } = new();
        public bool IsOnline { get; set; }
    }
}
