using System.IO;
using System.Management;
using Microsoft.Win32;

namespace MasterRgb.Services;

/// <summary>Diagnóstico somente leitura. Nunca altera BIOS, drivers ou políticas.</summary>
public sealed class HardwareAnalyzerService
{
    private static string Wmi(string query, string field)
    {
        try
        {
            using var search = new ManagementObjectSearcher(query);
            foreach (ManagementObject item in search.Get())
            {
                string value = Convert.ToString(item[field]) ?? "";
                item.Dispose();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch { }
        return "Não disponível";
    }

    private static ulong Number(string query, string field)
    {
        ulong.TryParse(Wmi(query, field), out var result);
        return result;
    }

    public object Analyze()
    {
        var findings = new List<object>();
        void Add(string severity, string category, string title, string description, string recommendation, string impact)
            => findings.Add(new { severity, category, title, description, recommendation, impact });

        string cpu = Wmi("SELECT Name FROM Win32_Processor", "Name");
        string gpu = Wmi("SELECT Name FROM Win32_VideoController", "Name");
        string board = Wmi("SELECT Manufacturer FROM Win32_BaseBoard", "Manufacturer") + " " +
                       Wmi("SELECT Product FROM Win32_BaseBoard", "Product");
        string bios = Wmi("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion");
        string os = Wmi("SELECT Caption FROM Win32_OperatingSystem", "Caption");
        ulong ramBytes = Number("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory");
        double ramGb = Math.Round(ramBytes / 1073741824d, 1);
        ulong freeKb = Number("SELECT FreePhysicalMemory FROM Win32_OperatingSystem", "FreePhysicalMemory");
        double freeRamGb = Math.Round(freeKb / 1048576d, 1);
        string memorySpeed = Wmi("SELECT ConfiguredClockSpeed FROM Win32_PhysicalMemory", "ConfiguredClockSpeed");
        string memoryRatedSpeed = Wmi("SELECT Speed FROM Win32_PhysicalMemory", "Speed");

        if (ramGb > 0 && ramGb < 8)
            Add("high", "Hardware", "Memória RAM limitada", "O sistema detectou menos de 8 GB de RAM.",
                "Considere ampliar a RAM se a placa-mãe permitir. Verifique compatibilidade e capacidade máxima antes de comprar.", "Pode reduzir travamentos com vários programas abertos.");
        else if (ramGb > 0 && freeRamGb / ramGb < .15)
            Add("medium", "Windows", "Pouca RAM disponível", "A memória disponível estava abaixo de 15% no momento da análise.",
                "Feche programas pesados e revise a inicialização do Windows. Confirme se o uso elevado é recorrente.", "Pode melhorar a resposta do sistema sob carga.");

        if (int.TryParse(memorySpeed, out int configured) && int.TryParse(memoryRatedSpeed, out int rated) && rated > 0 && configured > 0 && configured < rated * .85)
            Add("info", "BIOS", "Verificar perfil XMP / EXPO", $"Memória configurada em {configured} MT/s; velocidade informada pelo módulo: {rated} MT/s.",
                "Confira no manual da placa-mãe se XMP/EXPO é compatível. Ativar o perfil pode causar instabilidade e exige testes; o MASTER HUB não modifica a BIOS.", "Pode aumentar a largura de banda da memória.");

        string driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        double diskFreePct = -1;
        double diskFreeGb = 0;
        try
        {
            var disk = new DriveInfo(driveRoot);
            if (disk.IsReady && disk.TotalSize > 0)
            {
                diskFreePct = Math.Round(disk.AvailableFreeSpace * 100d / disk.TotalSize, 1);
                diskFreeGb = Math.Round(disk.AvailableFreeSpace / 1073741824d, 1);
                if (diskFreePct < 15)
                    Add("high", "Armazenamento", "Pouco espaço livre na unidade do Windows", $"Restam {diskFreeGb} GB ({diskFreePct}%).",
                        "Use a análise de limpeza do OPTIMIZE SYSTEM e remova apenas arquivos conhecidos. Não apague arquivos do sistema manualmente.", "Ajuda atualizações e reduz problemas causados por falta de espaço.");
            }
        }
        catch { }

        string diskModel = Wmi("SELECT Model FROM Win32_DiskDrive", "Model");
        string diskType = Wmi("SELECT MediaType FROM MSFT_PhysicalDisk", "MediaType");
        if (diskType == "3")
            Add("medium", "Hardware", "Unidade mecânica detectada", "O computador possui ao menos um HDD.",
                "Se o Windows estiver instalado em um HDD, migrar para SSD pode melhorar significativamente inicialização e carregamentos. Confirme a unidade do sistema antes de decidir.", "Melhora potencial no tempo de resposta.");

        string power = Wmi("SELECT BatteryStatus FROM Win32_Battery", "BatteryStatus");
        if (power != "Não disponível")
            Add("info", "Windows", "Perfil de energia para notebook", "Foi detectada uma bateria.",
                "Compare o modo de energia em tomada e bateria. O modo de desempenho máximo pode aumentar consumo, calor e ruído.", "Equilíbrio entre autonomia e desempenho.");

        if (findings.Count == 0)
            Add("info", "Sistema", "Nenhum gargalo evidente nas verificações básicas", "Os indicadores consultados não apontaram problemas óbvios.",
                "Monitore temperaturas e utilização sob carga com uma ferramenta de sensores confiável; este diagnóstico não mede temperatura nem estabilidade.", "Diagnóstico inicial, não benchmark.");

        Add("info", "BIOS", "Revisão de BIOS e drivers", $"Placa-mãe: {board}. BIOS detectada: {bios}.",
            "Consulte exclusivamente o site do fabricante para verificar BIOS, chipset e drivers compatíveis. Não atualize BIOS automaticamente; faça backup e siga o procedimento oficial.", "Compatibilidade, segurança e estabilidade, conforme o caso.");

        return new
        {
            hardware = new { cpu, gpu, board, bios, os, ramGb, freeRamGb, memorySpeed, memoryRatedSpeed, diskModel, diskFreeGb, diskFreePct },
            findings,
            disclaimer = "Análise somente leitura baseada em informações fornecidas pelo Windows. Não detecta todas as temperaturas, falhas físicas, configurações de BIOS ou gargalos de jogos."
        };
    }
}
