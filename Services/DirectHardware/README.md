# MASTER RGB 0.7.0 — detecção nativa (somente leitura)

Esta pasta substitui `Services/DirectHardware` da instalação existente.

## O que muda

- Amplia o inventário Windows PnP para todas as coleções HID e dispositivos USB com VID/PID.
- Inclui PCI de NVIDIA, AMD, MSI e controladores de chipset AMD/Intel relacionados à investigação de GPU/SMBus.
- Classifica cada registro por família, relevância para RGB e próximo passo recomendado.
- Mantém o inventário estritamente somente leitura: não abre interfaces, não envia relatórios HID e não executa transações I2C/SMBus.
- Mantém o driver Logitech G203 da v0.6.8 sem alterações.
- Mantém a interface/controle OpenRGB existente fora desta pasta.

## Como atualizar

1. Feche o MASTER RGB.
2. Faça uma cópia de segurança de `Services/DirectHardware`.
3. Extraia o ZIP de atualização.
4. Copie a pasta `Services/DirectHardware` do pacote para a pasta do MASTER RGB, escolhendo substituir os arquivos existentes.
5. Abra o projeto e execute `GERAR_EXE.bat` (ou o script `publish-win-x64.ps1`).
6. Abra o executável em `publish/win-x64/MASTER RGB.exe`.
7. Clique em **Diagnóstico completo** e salve o JSON.

O JSON passa a incluir `DeviceFamily`, `RgbRelevance` e `RecommendedNextStep` em cada dispositivo PnP. Também inclui registros HID/USB/PCI que antes eram descartados pelo filtro limitado de fabricantes.

## Limites e segurança

A presença de um dispositivo no inventário não significa que ele tenha RGB controlável. A identificação PnP não confirma protocolo, endereço SMBus, compatibilidade elétrica ou capacidade de escrita. O controle nativo da MSI B550 e da RTX 3060 ainda não está implementado nesta atualização. O G203 já funcional não foi modificado.
