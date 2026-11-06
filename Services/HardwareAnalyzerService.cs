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

    // API documentada do Windows: consulta a disponibilidade de tabelas SMBIOS/ACPI.
    // Tabelas de firmware não incluem obrigatoriamente a configuração XMP/EXPO.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint providerSignature, uint tableId, IntPtr buffer, uint bufferSize);

    private static string FirmwareTableObservation()
    {
        try
        {
            // 'RSMB' (Raw SMBIOS Data) em ordem little-endian.
            uint size = GetSystemFirmwareTable(0x424D5352, 0, IntPtr.Zero, 0);
            return size > 0 ? "Tabela SMBIOS bruta disponível (" + size + " bytes); não contém indicador padronizado de XMP/EXPO." :
                "Tabela SMBIOS bruta indisponível pela API do Windows.";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
        {
            return "API de tabelas de firmware indisponível.";
        }
    }

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
        string biosManufacturer = Wmi("SELECT Manufacturer FROM Win32_BIOS", "Manufacturer");
        string biosReleaseDate = Wmi("SELECT ReleaseDate FROM Win32_BIOS", "ReleaseDate");
        string biosFirmwareObservation = FirmwareTableObservation();
        string firmwareBootMode = "Não disponível";
        try
        {
            if (GetFirmwareType(out uint bootType))
                firmwareBootMode = bootType == 2 ? "UEFI" : bootType == 1 ? "Legacy BIOS" : "Desconhecido";
        }
        catch { }

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
        {
            string tutorialUrl = "";
            string tutorialTitle = "";
            bool bootWarning = false;
            if (name.StartsWith("XMP / EXPO", StringComparison.Ordinal))
            {
                tutorialUrl = "https://www.asus.com/br/support/faq/1042256/";
                tutorialTitle = "Tutorial oficial ASUS: configurar XMP / EXPO / DOCP";
                bootWarning = true;
            }
            else if (name.StartsWith("Resizable BAR", StringComparison.Ordinal) || name == "Above 4G Decoding")
            {
                tutorialUrl = "https://rog.asus.com/br/support/faq/1046107/";
                tutorialTitle = "Tutorial oficial ASUS: configurar Resizable BAR";
                bootWarning = true;
            }
            else if (name.Contains("CSM", StringComparison.Ordinal) || name.StartsWith("Modo de inicialização", StringComparison.Ordinal))
            {
                tutorialUrl = "https://support.microsoft.com/pt-br/windows/security/devicesecurity/windows-11-and-secure-boot";
                tutorialTitle = "Guia oficial Microsoft: UEFI, Legacy e CSM";
                bootWarning = true;
            }
            bool isMsi = board.Contains("MSI", StringComparison.OrdinalIgnoreCase) || board.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase);
            bool isAsus = board.Contains("ASUS", StringComparison.OrdinalIgnoreCase) || board.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase);
            bool isAsrock = board.Contains("ASRock", StringComparison.OrdinalIgnoreCase);
            bool isGigabyte = board.Contains("GIGABYTE", StringComparison.OrdinalIgnoreCase) || board.Contains("AORUS", StringComparison.OrdinalIgnoreCase);
            bool isXmp = name.StartsWith("XMP / EXPO", StringComparison.Ordinal);
            bool isRebar = name.StartsWith("Resizable BAR", StringComparison.Ordinal) || name == "Above 4G Decoding";
            bool isCsm = name.Contains("CSM", StringComparison.Ordinal) || name.StartsWith("Modo de inicialização", StringComparison.Ordinal);
            string vendor = isMsi ? "MSI" : isAsus ? "ASUS" : isAsrock ? "ASRock" : isGigabyte ? "Gigabyte / AORUS" : "Fabricante não identificado";
            // Links oficiais: não misturar tutoriais de outra fabricante.
            if (isMsi)
            {
                if (isXmp) { tutorialUrl = "https://br.msi.com/blog/how-to-speed-up-your-ram-with-overclocking-the-ultimate-guide-to-enabling-xmp-and-expo"; tutorialTitle = "MSI — guia XMP / EXPO"; }
                else if (isRebar || isCsm) { tutorialUrl = "https://br.msi.com/Landing/msi-click-bios-intel-amd-motherboard"; tutorialTitle = "MSI — guia oficial Click BIOS (Re-Size BAR e opções de BIOS)"; }
                else { tutorialUrl = "https://br.msi.com/support/download"; tutorialTitle = "MSI — suporte e manuais da placa-mãe"; }
            }
            else if (isAsus)
            {
                if (isXmp) { tutorialUrl = "https://www.asus.com/br/support/faq/1042256/"; tutorialTitle = "ASUS — guia XMP / EXPO / DOCP"; }
                else if (isRebar || isCsm) { tutorialUrl = "https://www.asus.com/br/support/faq/1046107/"; tutorialTitle = "ASUS — guia Resizable BAR e CSM"; }
                else { tutorialUrl = "https://www.asus.com/br/support/"; tutorialTitle = "ASUS — suporte e manuais da placa-mãe"; }
            }
            else if (isAsrock)
            {
                if (isXmp) { tutorialUrl = "https://www.asrock.com/support/faq.asp?k=ram"; tutorialTitle = "ASRock — orientações oficiais de XMP e compatibilidade da RAM"; }
                else if (isRebar || isCsm) { tutorialUrl = "https://www.asrock.com/support/faq.br.asp?id=498"; tutorialTitle = "ASRock — guia C.A.M. / Resizable BAR / CSM"; }
                else { tutorialUrl = "https://www.asrock.com/support/index.asp"; tutorialTitle = "ASRock — suporte e manuais da placa-mãe"; }
            }
            else if (isGigabyte)
            {
                if (isXmp) { tutorialUrl = "https://download.gigabyte.com/FileList/Manual/mb_manual_intel700-X-bios_e.pdf"; tutorialTitle = "Gigabyte / AORUS — manual de referência XMP (Intel 700; consulte o manual do seu modelo)"; }
                else if (isRebar || isCsm) { tutorialUrl = "https://www.gigabyte.com/WebPage/785/NVIDIA_resizable_bar.html"; tutorialTitle = "Gigabyte / AORUS — guia Resizable BAR / Above 4G / CSM"; }
                else { tutorialUrl = "https://www.gigabyte.com/Support"; tutorialTitle = "Gigabyte / AORUS — suporte e manuais"; }
            }
            else
            {
                tutorialUrl = "";
                tutorialTitle = "";
            }
            biosChecks.Add(new { name, state, recommendation, reason, steps, caution, status, tutorialUrl, tutorialTitle, bootWarning, motherboardVendor = vendor });
        }

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

        // Diagnóstico por módulo: SMBIOS não expõe perfis SPD/XMP e não prova o estado da BIOS.
        var memoryModules = new List<object>();
        var speedPairs = new List<(int configured, int rated)>();
        var memoryModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moduleObservations = new List<string>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT DeviceLocator,BankLabel,Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject module in search.Get())
            {
                using (module)
                {
                    string location = Convert.ToString(module["DeviceLocator"])?.Trim() ?? "";
                    string model = Convert.ToString(module["PartNumber"])?.Trim() ?? "";
                    string manufacturer = Convert.ToString(module["Manufacturer"])?.Trim() ?? "";
                    string bank = Convert.ToString(module["BankLabel"])?.Trim() ?? "";
                    int.TryParse(Convert.ToString(module["Speed"]), out int ratedSpeed);
                    int.TryParse(Convert.ToString(module["ConfiguredClockSpeed"]), out int configuredSpeed);
                    int.TryParse(Convert.ToString(module["SMBIOSMemoryType"]), out int smbiosType);
                    ulong.TryParse(Convert.ToString(module["Capacity"]), out ulong capacityBytes);
                    if (!string.IsNullOrWhiteSpace(model)) memoryModels.Add(model);
                    if (ratedSpeed > 0 && configuredSpeed > 0) speedPairs.Add((configuredSpeed, ratedSpeed));
                    string slot = !string.IsNullOrWhiteSpace(location) ? location : !string.IsNullOrWhiteSpace(bank) ? bank : "Slot não identificado";
                    string moduleType = smbiosType switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => "Tipo não identificado" };
                    memoryModules.Add(new { slot, bank, manufacturer, model, capacityGb = Math.Round(capacityBytes / 1073741824d, 1), moduleType, configuredMt = configuredSpeed, reportedMt = ratedSpeed, profileState = "XMP/EXPO não verificável por SMBIOS" });
                    moduleObservations.Add(slot + ": " + (configuredSpeed > 0 ? configuredSpeed + " MT/s configurados" : "clock indisponível") +
                        (ratedSpeed > 0 ? ", " + ratedSpeed + " MT/s informados" : "") +
                        (model.Length > 0 ? ", " + model : ""));
                }
            }
        }
        catch { }
        bool memoryBelowRated = speedPairs.Any(x => x.configured < x.rated * .85);
        bool mixedModels = memoryModels.Count > 1;
        bool incompleteMemoryData = memoryModules.Count == 0 || speedPairs.Count != memoryModules.Count;
        string firmwareEvidence = "Placa-mãe: " + board + "; BIOS: " + biosManufacturer + " " + bios +
            "; data da BIOS (WMI): " + biosReleaseDate + "; boot: " + firmwareBootMode + ". " + biosFirmwareObservation;
        string memoryObservation = moduleObservations.Count > 0
            ? string.Join("; ", moduleObservations) + ". Speed/ConfiguredClockSpeed são dados SMBIOS, NÃO são perfis XMP/EXPO extraídos do SPD."
            : "Não foi possível obter dados por módulo.";
        string memoryAdvice = incompleteMemoryData
            ? "VERIFICAR na BIOS: não há dados suficientes para concluir se a memória está na velocidade esperada."
            : memoryBelowRated
                ? "VERIFICAR o perfil XMP/EXPO na BIOS: um ou mais módulos operam abaixo da velocidade informada pelo SMBIOS. Confirme o perfil suportado antes de ATIVAR."
                : "MANTER se estiver estável; os clocks consultados não mostram diferença expressiva. Isso NÃO confirma XMP/EXPO ativo.";
        if (mixedModels) memoryAdvice += " Existem modelos diferentes de RAM; isso não significa, por si só, instabilidade.";
        // Detector universal conservador: funciona com os campos SMBIOS de qualquer fabricante.
        // Não deduz ativação do XMP a partir de velocidades, nomes comerciais ou placa-mãe.
        bool allModulesMeasured = speedPairs.Count > 0 && speedPairs.Count == memoryModules.Count;
        bool sameConfiguredSpeed = allModulesMeasured && speedPairs.Select(x => x.configured).Distinct().Count() == 1;
        bool allBelowReported = allModulesMeasured && speedPairs.All(x => x.configured < x.rated * .85);
        bool someBelowReported = allModulesMeasured && speedPairs.Any(x => x.configured < x.rated * .85);
        string memoryState = !allModulesMeasured
            ? "XMP / EXPO não verificável: dados de frequência incompletos"
            : !sameConfiguredSpeed
                ? "Frequências informadas diferentes entre módulos; conferir dados da BIOS"
                : "XMP / EXPO não confirmado; frequência observada: " + speedPairs[0].configured + " MT/s";
        if (allModulesMeasured)
        {
            memoryAdvice = allBelowReported
                ? "Todos os módulos estão abaixo da velocidade informada no SMBIOS. VERIFIQUE se há perfil XMP/EXPO compatível na BIOS; isso não prova que está desativado."
                : someBelowReported
                    ? "Há diferença de velocidade em parte dos módulos. CONFIRA compatibilidade, perfis e estabilidade na BIOS."
                    : "A memória opera na velocidade informada pelo SMBIOS. MANTENHA se estável; isso não comprova que XMP/EXPO está ativado.";
        }
        memoryObservation += " Diagnóstico universal baseado em dados Windows/SMBIOS, sem tabela fixa por marca. Um ajuste manual pode reproduzir o mesmo clock de um perfil XMP/EXPO.";
        BiosCheck("XMP / EXPO / DOCP",
            memoryState,
            memoryAdvice,
            memoryObservation + " Firmware: " + firmwareEvidence,
            "1. Confira o modelo e o perfil XMP/EXPO de cada módulo no manual do fabricante. 2. Abra a BIOS e identifique o estado de A-XMP/XMP/EXPO. 3. Compare frequência e timings do perfil com a configuração em uso. 4. Se compatível, ative um perfil e teste estabilidade; se falhar, reverta.",
            "A leitura de SPD exige acesso específico ao hardware e não foi implementada. Não confunda velocidade SMBIOS com prova de XMP ativo. Perfis podem causar instabilidade e falha de inicialização.",
            someBelowReported ? "attention" : "unknown");

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
            hardware = new { cpu, gpu, board, bios, biosManufacturer, biosReleaseDate, firmwareBootMode, biosFirmwareObservation, os, ramGb, freeRamGb, memorySpeed, memoryRatedSpeed, diskModel, diskFreeGb, diskFreePct },
            findings,
            ramSticks, memoryModules, videoCards, storageDevices, processors,
            biosChecks,
            disclaimer = "Análise somente leitura baseada em informações fornecidas pelo Windows. Não detecta todas as temperaturas, falhas físicas, configurações de BIOS ou gargalos de jogos."
        };
    }
}
