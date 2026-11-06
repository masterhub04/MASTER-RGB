using System.IO;
using System.Runtime.InteropServices;
using System.Management;
using Microsoft.Win32;

namespace MasterRgb.Services;

/// <summary>Diagnóstico somente leitura. Nunca altera BIOS, drivers ou políticas.</summary>
public sealed class HardwareAnalyzerService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

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

        var ramSticks = new List<object>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Manufacturer,PartNumber,DeviceLocator,Capacity,Speed FROM Win32_PhysicalMemory");
            foreach (ManagementObject part in search.Get())
            {
                ramSticks.Add(new { manufacturer = Convert.ToString(part["Manufacturer"]), model = Convert.ToString(part["PartNumber"]), slot = Convert.ToString(part["DeviceLocator"]), bytes = Convert.ToString(part["Capacity"]), speed = Convert.ToString(part["Speed"]) });
                part.Dispose();
            }
        }
        catch { }

        var videoCards = new List<object>();
        var storageDevices = new List<object>();
        var processors = new List<object>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name,AdapterRAM,DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject part in search.Get())
            {
                videoCards.Add(new { name = Convert.ToString(part["Name"]), vramBytes = Convert.ToString(part["AdapterRAM"]), driver = Convert.ToString(part["DriverVersion"]) });
                part.Dispose();
            }
        }
        catch { }
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Model,Size,InterfaceType,MediaType FROM Win32_DiskDrive");
            foreach (ManagementObject part in search.Get())
            {
                storageDevices.Add(new { model = Convert.ToString(part["Model"]), bytes = Convert.ToString(part["Size"]), interfaceType = Convert.ToString(part["InterfaceType"]), mediaType = Convert.ToString(part["MediaType"]) });
                part.Dispose();
            }
        }
        catch { }
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject part in search.Get())
            {
                processors.Add(new { name = Convert.ToString(part["Name"]), cores = Convert.ToString(part["NumberOfCores"]), threads = Convert.ToString(part["NumberOfLogicalProcessors"]) });
                part.Dispose();
            }
        }
        catch { }

        // A maioria das opções de firmware não tem API de leitura padronizada no Windows.
        // Nunca inferir que XMP/EXPO ou ReBAR estão ativos somente pela presença do hardware.
        var biosChecks = new List<object>();
        void BiosCheck(string name, string state, string recommendation, string reason, string steps, string caution, string status = "unknown")
            => biosChecks.Add(new { name, state, recommendation, reason, steps, caution, status });

        bool intel = cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        bool amd = cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        bool laptop = power != "Não disponível";
        string firmware = "Não foi possível verificar";
        try
        {
            if (GetFirmwareType(out uint type))
                firmware = type switch { 2 => "UEFI detectado", 1 => "BIOS legado detectado", _ => "Não foi possível verificar" };
        }
        catch { }

        // Win32_Processor expõe o estado da virtualização do firmware em vários sistemas.
        string virtualizationRaw = Wmi("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor", "VirtualizationFirmwareEnabled");
        string virtualization = virtualizationRaw.Equals("True", StringComparison.OrdinalIgnoreCase)
            ? "Ativada (informação do processador)"
            : virtualizationRaw.Equals("False", StringComparison.OrdinalIgnoreCase)
                ? "Desativada (informação do processador)"
                : "Não foi possível verificar";

        // Comparação por módulo, sem inferir estado de XMP/EXPO apenas pelo clock.
        var speedPairs = new List<(int configured, int rated)>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Speed,ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            foreach (ManagementObject module in search.Get())
            {
                using (module)
                {
                    if (int.TryParse(Convert.ToString(module["Speed"]), out int ratedSpeed) &&
                        int.TryParse(Convert.ToString(module["ConfiguredClockSpeed"]), out int configuredSpeed) &&
                        ratedSpeed > 0 && configuredSpeed > 0)
                        speedPairs.Add((configuredSpeed, ratedSpeed));
                }
            }
        }
        catch { }
        bool memoryBelowRated = speedPairs.Any(x => x.configured < x.rated * .85);
        string memoryObservation = speedPairs.Count > 0
            ? "Módulos consultados: " + string.Join("; ", speedPairs.Select((x, i) => $"RAM {i + 1}: {x.configured} / {x.rated} MT/s (configurada / informada)")) +
              ". Esses números não comprovam se XMP/EXPO está ativado."
            : "Não foi possível comparar a frequência configurada de cada módulo.";
        string memoryAdvice = memoryBelowRated
            ? "VERIFICAR XMP/EXPO: ao menos um módulo está configurado abaixo da velocidade informada. Considere ATIVAR um perfil compatível após conferir suporte e estabilidade."
            : "NÃO ALTERAR apenas por esta comparação: frequências iguais não comprovam XMP/EXPO ativo. Confira o perfil na BIOS.";
        BiosCheck("XMP / EXPO / DOCP", "Perfil não verificável diretamente; " + (speedPairs.Count > 0 ? "frequência observada: " + string.Join(", ", speedPairs.Select(x => x.configured).Distinct()) + " MT/s" : "sem dados de frequência"),
            memoryAdvice,
            memoryObservation,
            "1. Consulte o manual da placa-mãe e a especificação da RAM. 2. Reinicie e entre no Setup (geralmente Del ou F2). 3. Abra OC / Ai Tweaker / Extreme Tweaker / Tweaker. 4. Localize XMP, EXPO ou DOCP; selecione um perfil suportado. 5. Salve com F10, reinicie e teste estabilidade.",
            "Perfis de memória são overclock em muitas plataformas; podem causar falhas de boot, instabilidade ou exigir recuperação de configurações.", memoryBelowRated ? "attention" : "unknown");

        bool rebarFamily = System.Text.RegularExpressions.Regex.IsMatch(gpu, @"RTX\s*(30|40|50)\d{2}|RX\s*(6|7|9)\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string rebarNote = rebarFamily
            ? "Família de GPU com suporte comum a ReBAR/SAM identificada; confirme modelo, VBIOS, driver, placa-mãe e estado efetivo."
            : "Compatibilidade da GPU não confirmada apenas pelo nome detectado.";
        BiosCheck("Resizable BAR / Smart Access Memory", "Não foi possível verificar",
            rebarFamily ? "VERIFICAR no painel do driver se ReBAR está ativo; se não estiver, avaliar ATIVAR após confirmar suporte." : "VERIFICAR compatibilidade antes de considerar ATIVAR.",
            rebarNote + " O WMI não confirma se o recurso está ativo.",
            "1. Confira suporte da GPU e da placa-mãe no site dos fabricantes. 2. Verifique o estado no painel do driver da GPU (ex.: Informações do sistema da NVIDIA) ou ferramenta confiável. 3. Na BIOS, procure Advanced / PCIe / PCI Subsystem Settings. 4. Com sistema UEFI/GPT compatível, configure Above 4G Decoding e Re-Size BAR Support conforme manual. 5. Salve e confirme o estado no driver.",
            "Não desative CSM nem mude modo de boot sem confirmar UEFI/GPT: o Windows pode deixar de iniciar.",
            rebarFamily && firmware == "UEFI detectado" ? "attention" : "unknown");

        BiosCheck("Above 4G Decoding", "Não foi possível verificar",
            "Considerar ATIVAR quando exigido pelo Resizable BAR e houver suporte.",
            "Permite mapear recursos PCIe acima de 4 GB de espaço de endereçamento; sozinho não garante ganho de FPS.",
            "1. Entre na BIOS (Del/F2). 2. Procure Advanced / PCI Subsystem Settings. 3. Localize Above 4G Decoding. 4. Ative somente se o manual indicar compatibilidade e necessidade. 5. Salve e valide o funcionamento.",
            "Pode exigir firmware/boot compatíveis; registre o valor original antes de alterar.");

        BiosCheck("Modo de inicialização (UEFI / Legacy)", firmware,
            firmware == "UEFI detectado" ? "MANTER o boot UEFI; não há motivo para mudar CSM apenas por desempenho." : "VERIFICAR compatibilidade antes de considerar DESATIVAR CSM para usar Resizable BAR.",
            "Resizable BAR frequentemente requer boot UEFI com CSM desativado; a mudança não é uma otimização universal de desempenho.",
            "1. No Windows, execute msinfo32 e confira Modo da BIOS. 2. Em Gerenciamento de Disco, confira se o disco de boot usa GPT. 3. Consulte o manual e confirme suporte UEFI da GPU. 4. Só então avalie Boot / CSM Support na BIOS; não altere se o Windows depender de boot legado.",
            "Alterar CSM/Legacy/UEFI sem preparar a instalação pode impedir o Windows de iniciar.", firmware == "UEFI detectado" ? "good" : firmware == "BIOS legado detectado" ? "attention" : "unknown");

        BiosCheck("CSM (Compatibility Support Module)", "Estado do CSM não acessível diretamente; " + firmware,
            firmware == "BIOS legado detectado" ? "NÃO DESATIVAR AGORA: o Windows iniciou em modo legado; prepare a migração para UEFI/GPT antes de mudar CSM." : firmware == "UEFI detectado" ? "VERIFICAR na BIOS: se CSM estiver ativo, considere DESATIVAR apenas após confirmar compatibilidade e necessidade para ReBAR." : "VERIFICAR na BIOS; não alterar CSM sem confirmar o modo de boot.",
            "O modo de boot do Windows não equivale ao estado da opção CSM na placa-mãe.",
            "1. Entre na BIOS. 2. Procure Settings / Boot / CSM Support. 3. Confira se o disco de boot é GPT e se a GPU suporta UEFI antes de qualquer alteração.",
            "Desativar CSM sem preparar o sistema pode impedir o Windows de iniciar.");

        string currentCpuClock = Wmi("SELECT CurrentClockSpeed FROM Win32_Processor", "CurrentClockSpeed");
        string maxCpuClock = Wmi("SELECT MaxClockSpeed FROM Win32_Processor", "MaxClockSpeed");
        BiosCheck("Turbo Boost / Precision Boost", "Estado não confirmado; clock atual: " + currentCpuClock + " MHz; clock nominal: " + maxCpuClock + " MHz",
            "Em geral, MANTER ATIVADO no perfil padrão do fabricante, se disponível.",
            "Permite ao processador aumentar dinamicamente a frequência dentro dos limites de potência e temperatura.",
            "1. Consulte o modelo do processador e manual da placa-mãe. 2. Entre na BIOS. 3. Procure CPU Configuration / Advanced CPU Settings / AMD CBS. 4. Verifique Intel Turbo Boost ou AMD Core Performance Boost. 5. Prefira Auto/Enabled conforme especificação original.",
            "Não confundir com overclock manual, Multi-Core Enhancement ou remoção de limites de potência; calor e consumo podem aumentar.");

        if (amd)
            BiosCheck("Precision Boost Overdrive (PBO)", "Não foi possível verificar",
                "MANTER AUTO/DESATIVADO inicialmente; ativar apenas com refrigeração adequada e testes.",
                "Pode elevar desempenho em algumas cargas, mas aumenta consumo e temperatura e nem sempre traz ganhos perceptíveis.",
                "1. Verifique suporte do modelo Ryzen e da placa-mãe. 2. Na BIOS, procure AMD Overclocking / Precision Boost Overdrive. 3. Compare Auto e Enabled apenas após monitorar temperatura, potência e estabilidade. 4. Reverta se houver instabilidade.",
                "PBO altera limites operacionais; não é recomendado como ajuste automático.");
        if (intel)
            BiosCheck("Intel MCE / Enhanced Turbo / limites de potência", "Não foi possível verificar",
                "PREFERIR Intel Default Settings e limites oficiais; não liberar potência indiscriminadamente.",
                "Perfis agressivos de placa-mãe podem elevar temperatura e consumo sem melhorar o desempenho sustentado.",
                "1. Consulte o suporte da CPU e da placa-mãe. 2. Na BIOS, procure CPU Power Management / Intel Default Settings / Multi-Core Enhancement. 3. Prefira o perfil padrão oficial do processador. 4. Teste temperatura e estabilidade.",
                "Desativar limites térmicos ou elétricos pode causar instabilidade e desgaste.");

        BiosCheck("Curva das ventoinhas / Fan Control", "Não foi possível verificar",
            "VERIFICAR se o perfil térmico é adequado; ajustar com cautela se houver aquecimento ou ruído excessivo.",
            "Temperaturas altas podem causar redução automática de frequência; uma curva adequada pode preservar desempenho sustentado.",
            "1. Meça temperaturas sob carga com ferramenta de sensores confiável. 2. Entre na BIOS e procure Q-Fan / Smart Fan / Hardware Monitor. 3. Confira tipo PWM/DC e ajuste gradualmente a curva. 4. Teste temperatura e ruído após salvar.",
            "Nunca desative proteção térmica, ventoinha da CPU ou limite crítico de temperatura.");

        BiosCheck("Virtualização (VT-x / SVM)", virtualization,
            virtualization.StartsWith("Desativada") ? "ATIVAR somente se você usa Hyper-V, WSL2, máquinas virtuais ou recursos que exigem virtualização; não há ganho universal de FPS." : "MANTER conforme necessidade; não desativar só para tentar ganhar FPS.",
            "É importante para Hyper-V, WSL2, máquinas virtuais e recursos de segurança; desativá-la não é otimização universal.",
            "1. Abra Gerenciador de Tarefas > Desempenho > CPU e veja Virtualização. 2. Se precisar alterar, procure Intel Virtualization Technology ou SVM Mode na BIOS. 3. Só mude após verificar dependências de segurança e software.",
            "Pode impedir funcionamento de VMs, WSL2 e recursos de segurança.", virtualization.StartsWith("Ativada") ? "good" : "unknown");

        if (laptop)
            BiosCheck("Modo de desempenho / térmico do notebook", "Não foi possível verificar",
                "Usar o perfil de desempenho do fabricante na tomada quando necessário, mantendo proteção térmica.",
                "Notebooks têm limites próprios de temperatura, potência e ruído.",
                "1. Abra o aplicativo oficial do fabricante do notebook. 2. Compare perfis Balanceado e Desempenho. 3. Se houver opção na BIOS, consulte o manual antes de mudar. 4. Monitore temperatura e ruído.",
                "Não desative limites térmicos ou de proteção da bateria.");

        return new
        {
            hardware = new { cpu, gpu, board, bios, os, ramGb, freeRamGb, memorySpeed, memoryRatedSpeed, diskModel, diskFreeGb, diskFreePct },
            findings,
            ramSticks, videoCards, storageDevices, processors,
            biosChecks,
            disclaimer = "Análise somente leitura baseada em informações fornecidas pelo Windows. Não detecta todas as temperaturas, falhas físicas, configurações de BIOS ou gargalos de jogos."
        };
    }
}
