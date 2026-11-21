# Luma Video Downloader — especificação e plano de implementação

**Versão:** 2.0  
**Data:** 20/11/2026  
**Plataforma de distribuição:** Windows 10/11 x64  
**Estado:** implementação, smoke tests e EXE portátil validados localmente

## 1. Visão geral

Aplicativo desktop WinForms que recebe um link HTTP/HTTPS e salva a mídia autorizada como MP3 ou MP4. O usuário escolhe o bitrate MP3 ou a resolução máxima MP4. Um link de playlist ou mix é processado por inteiro, item a item, e organizado em uma pasta com o nome da lista. Uma aba separada mostra os downloads concluídos.

Interface, download, conversão e histórico são locais. A distribuição principal é um único executável portátil autocontido com o app e as ferramentas externas; na primeira execução, ele prepara as ferramentas automaticamente no perfil do usuário, sem instalador. Há também um instalador opcional. O conteúdo remoto não passa por servidor próprio.

O programa não contorna DRM, login, cookies, conteúdo privado nem restrições de acesso. Use-o somente para conteúdo cujo download seja permitido pelo titular e pelas regras da origem.

## 2. Escopo funcional

### Incluído

- URLs HTTP/HTTPS de mídia compatível.
- Seleção de destino e gravação opcional da preferência de pasta.
- Formatos MP3 e MP4.
- Bitrate MP3 selecionável entre 64 e 320 kbps, padrão 192 kbps.
- Resolução máxima MP4 selecionável entre 320p, 720p, 1080p e 4K (2160p), padrão 720p. Saída H.264/AAC; vídeos menores não são ampliados.
- Download sequencial de todas as entradas de playlists/mixes reconhecidas pelo yt-dlp; saída agrupada em pasta com nome e índice da lista. Itens indisponíveis podem ser ignorados, com resultado parcial informado.
- Aba local **Downloads feitos**, com arquivo, formato, bitrate, horário e ação para abrir a localização. Até 2.000 registros em JSON.
- Status, progresso de download, conversão, cancelamento e mensagens em português.
- Progresso com etapa atual, tempo decorrido e contagem de itens da playlist sempre que a origem reportar esses dados.
- Aba persistente de downloads não concluídos com motivo resumido por item.
- Arquivos temporários isolados por operação; sem sobrescrita de arquivos existentes.
- Executável portátil único para Windows x64 que incorpora o app e yt-dlp, Deno, FFmpeg e ffprobe; extrai as ferramentas automaticamente no primeiro uso.
- Instalador opcional para Windows x64 que baixa as ferramentas durante a instalação, validando hashes SHA-256 publicados.

### Fora do escopo

- Login, leitura de cookies, conteúdo privado, DRM ou contorno de proteção.
- Canal inteiro, fila de várias URLs, downloads paralelos ou agendamento.
- Edição de tags, capas, legendas, thumbnails, extração de voz/instrumental ou player embutido.
- Atualizações silenciosas em instalações já existentes.
- Distribuição para Windows ARM64, macOS ou Linux.

## 3. Fluxo do usuário

1. Para distribuição sem instalação, gerar `LumaVideoDownloader.exe` com `scripts/build-portable.ps1` e entregar esse único arquivo para cada computador Windows x64. O instalador opcional pode ser gerado com `scripts/build-installer.ps1`.
2. Na instalação, escolher uma pasta inexistente. O setup extrai o app autocontido, baixa os componentes mais recentes das fontes configuradas, confere hashes e move a instalação completa para a pasta escolhida. É necessária internet; a instalação por usuário não exige administrador.
3. No aplicativo, colar o link, escolher destino, formato e bitrate.
4. A aplicação valida URL/destino e verifica yt-dlp, Deno, FFmpeg e ffprobe antes de baixar.
5. yt-dlp processa todas as entradas da lista em sequência. MP3 é codificado no bitrate escolhido. MP4 é baixado em uma pasta temporária, convertido pelo FFmpeg para H.264/AAC até a resolução máxima escolhida e então gravado no destino sem ampliar vídeos menores.
6. Arquivos confirmados são registrados no histórico. Se alguns itens falharem, os que foram concluídos são mantidos e o status informa que o resultado é parcial.
7. Em falha ou cancelamento, os processos associados são encerrados e a pasta temporária da operação é removida. Itens preexistentes nunca são substituídos.

## 4. Interface

A janela principal contém duas abas:

- **Baixar:** URL, pasta, formato MP3/MP4, bitrate MP3 ou resolução máxima MP4, preferência de pasta, baixar/cancelar, status, progresso e resultado.
- **Downloads feitos:** lista de arquivos concluídos, formato, qualidade (bitrate MP3 ou resolução MP4), horário, atualizar e abrir o arquivo selecionado no Explorer.

MP3 aceita de 64 a 320 kbps. MP4 oferece 320p, 720p, 1080p e 4K (2160p) como resolução máxima, sem ampliar vídeos menores. O controle é ajustado ao formato selecionado. O aplicativo avisa que playlists e mixes são baixados por inteiro.

## 5. Arquitetura

- **WinForms / .NET 10 LTS:** aplicativo e instalador autocontidos.
- **`DownloadService`:** valida solicitação, executa yt-dlp por `ProcessStartInfo.ArgumentList`, usa pasta de trabalho exclusiva, converte MP4, confirma e move resultados sem sobrescrever.
- **`DependencyChecker`:** confirma existência e execução das quatro ferramentas externas locais.
- **`DownloadHistoryStore`:** JSON local atômico em `%LOCALAPPDATA%\MP3LocalDownloader\download-history.json` e retenção dos 2.000 registros mais recentes.
- **`InstallerService`:** extrai o payload do app com verificação de caminhos, baixa assets do GitHub e a build Windows Essentials do FFmpeg, valida SHA-256 e instala em uma pasta por usuário.
- **Modelos:** solicitação, formato, status, arquivo baixado e resultado de lote.

### Árvore esperada da instalação

```text
LumaVideoDownloader/
  LumaVideoDownloader.exe
  THIRD_PARTY_NOTICES.md
  tools/
    yt-dlp.exe
    deno.exe
    ffmpeg.exe
    ffprobe.exe
```

### Regras técnicas e de segurança

- Não montar comandos com shell; passar URL, caminhos e opções como argumentos individuais.
- Não salvar credenciais, cookies ou URLs de autenticação.
- Definir limite/retries de rede, ler stdout e stderr, manter a interface responsiva e matar a árvore de processos ao cancelar.
- Sanitizar/validar caminhos antes de mover saídas e limitar a limpeza à pasta temporária exclusiva da operação.
- Validar downloads do instalador com hashes SHA-256 publicados; falhar em caso de hash ausente ou divergente.
- Baixar versões atuais somente no ato da instalação; não fazer atualização silenciosa depois.

## 6. Requisitos consolidados

### Funcionais

| ID | Requisito |
|---|---|
| RF01 | Aceitar URL HTTP/HTTPS e validar os campos antes de executar ferramentas. |
| RF02 | Permitir destino gravável e persistir a preferência quando solicitada. |
| RF03 | Oferecer MP3 e MP4. |
| RF04 | Permitir selecionar bitrate MP3 de 64 a 320 kbps. |
| RF05 | Permitir selecionar resolução máxima MP4 de 320p, 720p, 1080p ou 4K (2160p), sem ampliar vídeos menores, e recodificar vídeo H.264/AAC. |
| RF06 | Baixar sequencialmente a lista/mix inteira que a origem disponibilizar ao yt-dlp e agrupar itens numa pasta da playlist. |
| RF07 | Tolerar itens indisponíveis quando possível, preservar arquivos concluídos e informar lote parcial. |
| RF08 | Exibir progresso, etapa, sucesso, erro e cancelamento sem travar a interface. |
| RF09 | Não substituir saídas preexistentes. |
| RF10 | Gravar os downloads concluídos no histórico local e permitir abrir a localização do arquivo. |
| RF11 | Instalar app e dependências em Windows x64 com um executável autocontido. |
| RF12 | Confirmar arquivos resultantes antes de mostrar sucesso e limpar temporários próprios. |

### Não funcionais

| ID | Requisito |
|---|---|
| RNF01 | Não exigir servidor, conta, Python ou runtime .NET separado na máquina de destino. |
| RNF02 | Usar somente HTTPS e fontes oficiais/configuradas para os componentes do instalador. |
| RNF03 | Suportar espaços e acentos em caminhos e nomes. |
| RNF04 | Manter a interface responsiva durante download e conversão. |
| RNF05 | Documentar fontes, licenças, versões e limites de distribuição. |
| RNF06 | Não contornar DRM, autenticação ou restrições do provedor. |

## 7. Implementação e verificação

- Serviço e interface WinForms com bitrate MP3, resolução MP4, lote sequencial, cancelamento e histórico JSON.
- Setup WinForms autocontido, payload do app embutido e download verificado de yt-dlp, Deno e FFmpeg.
- `Tests/MP3LocalDownloader.SmokeTests.csproj` cria mídia local e simula yt-dlp para verificar saída de dois itens, pastas, MP3 a 256 kbps, limite de resolução MP4 e conversão H.264, histórico, duplicados, erro, lote parcial e cancelamento.
- Os testes validam a exibição do item atual/contagem, a identificação do item que falhou e a persistência local da justificativa.
- Validar o pacote do instalador via `LumaVideoDownloader-Setup.exe --check-package`.
- Gerar `artifacts/portable/LumaVideoDownloader.exe` com `scripts/build-portable.ps1`; validar o pacote incorporado com `--check-bundle` e confirmar que o aplicativo abre a interface.
- O smoke test simula o comportamento de playlist para evitar depender da disponibilidade variável de uma playlist pública durante testes locais.

Comandos de verificação:

```powershell
dotnet build .\MP3LocalDownloader.csproj -c Release
dotnet run --project .\Tests\MP3LocalDownloader.SmokeTests.csproj -c Debug
dotnet publish .\Setup\MP3LocalDownloader.Setup.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\installer
.\artifacts\installer\LumaVideoDownloader-Setup.exe --check-package
.\scripts\build-portable.ps1
.\artifacts\portable\LumaVideoDownloader.exe --check-bundle
```

## 8. Tratamento de erros

| Situação | Comportamento |
|---|---|
| URL inválida ou esquema não HTTP(S) | Mostrar orientação; não iniciar ferramentas. |
| Origem indisponível, 403, 404 ou rate limit | Mostrar resumo acionável; não contornar restrição. |
| Ferramenta ausente/corrompida | Impedir início e apontar dependências. |
| Erro de item em playlist | Continuar quando yt-dlp permitir; salvar e registrar os itens concluídos e informar resultado parcial. |
| Conversão MP4 falha em um item | Pular o item, concluir os demais e informar lote parcial; se nenhum for convertido, mostrar erro. |
| Sem permissão ou espaço no destino | Mostrar erro e remover só a área temporária própria. |
| Arquivo duplicado | Ignorar o caminho já existente; nunca substituir. |
| Cancelamento | Encerrar processos filhos, remover a área temporária e restaurar controles. |
| Falha de instalação ou hash divergente | Não finalizar a instalação; remover staging próprio e informar erro. |

## 9. Critérios de aceite

| ID | Cenário | Esperado |
|---|---|---|
| T01 | MP3 com bitrate selecionado | MP3 reproduzível e bitrate confirmado pelo ffprobe. |
| T02 | MP4 com bitrate selecionado | MP4 reproduzível, H.264 e bitrate alvo passado ao FFmpeg. |
| T03 | Playlist/mix com vários itens | Todos os itens disponíveis são gravados em sequência numa pasta da lista. |
| T04 | Um item da playlist falha | Os outros concluídos permanecem e o status é parcial. |
| T05 | Mesmo item baixado de novo | Arquivo original preservado, sem substituição. |
| T06 | Aba de histórico | Mostra arquivos, formato, bitrate e horário, e abre o item selecionado. |
| T07 | Cancelamento em andamento | Processo termina e a pasta de trabalho própria é removida. |
| T08 | Link e título com caracteres acentuados | Caminhos e arquivos são criados corretamente. |
| T09 | Ferramenta ausente ou inválida | O aplicativo informa a dependência e não mostra falso sucesso. |
| T10 | Abrir o EXE portátil em Windows x64 sem .NET e sem ferramentas instaladas | O aplicativo extrai as ferramentas incluídas e abre sem um instalador separado. |
| T11 | Hash SHA-256 ausente ou divergente durante a criação do pacote | A compilação recusa o componente e não gera o executável portátil. |
| T12 | Arquivo de mídia/playlist não permitido | O aplicativo não tenta contornar DRM, login ou restrições. |

## 10. Referências técnicas

- [yt-dlp — releases, formato de saída e opção de playlist](https://github.com/yt-dlp/yt-dlp)
- [yt-dlp — runtime JavaScript e suporte ao YouTube](https://github.com/yt-dlp/yt-dlp/wiki/EJS)
- [Deno — releases oficiais](https://github.com/denoland/deno/releases)
- [.NET — política oficial de suporte](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [FFmpeg — downloads e opções de codecs](https://ffmpeg.org/download.html)
- [Builds Windows Essentials do FFmpeg](https://www.gyan.dev/ffmpeg/builds/)
