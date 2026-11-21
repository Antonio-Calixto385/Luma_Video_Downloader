# Luma Video Downloader

Aplicativo desktop WinForms para Windows 10/11 x64. Baixa mídia autorizada como MP3 ou MP4, permite escolher o bitrate do áudio MP3 ou a resolução máxima do vídeo MP4, processa playlists e mixes inteiros em sequência e registra os arquivos concluídos em uma aba de histórico. Todo o processamento ocorre no computador do usuário.

## Executável portátil (sem instalador)

Gere o executável autocontido, com o aplicativo e as ferramentas incluídos:

```powershell
.\scripts\build-portable.ps1
```

O arquivo pronto fica em `artifacts\portable\LumaVideoDownloader.exe`. Copie apenas esse EXE para um computador Windows 10/11 x64 e abra-o: não precisa instalar .NET, yt-dlp, Deno ou FFmpeg. Na primeira execução, o aplicativo extrai as ferramentas automaticamente para `%LOCALAPPDATA%\MP3LocalDownloader\portable`; não é necessário executar um instalador. O EXE pode ser movido ou copiado para outra pasta. A compilação do pacote requer internet para baixar e validar os componentes oficiais; o uso do aplicativo requer internet para acessar as origens de mídia.

Feche uma cópia que já esteja aberta antes de reconstruir o mesmo EXE. Se o Windows mantiver o arquivo bloqueado, o script preserva a cópia em uso e informa uma pasta `artifacts\portable-update-*` com o novo executável.

Os avisos de terceiros são extraídos junto às ferramentas. Consulte `THIRD_PARTY_NOTICES.md` antes de redistribuir o executável.

## Instalador alternativo

Compile o instalador autocontido:

```powershell
.\scripts\build-installer.ps1
```

O executável fica em `artifacts\installer\LumaVideoDownloader-Setup.exe`. Distribua esse arquivo para as máquinas Windows x64. Ao ser executado, o instalador incorpora o aplicativo autocontido, baixa yt-dlp, Deno e FFmpeg/ffprobe, valida os hashes SHA-256 publicados e instala em `%LOCALAPPDATA%\Programs\LumaVideoDownloader`. Ao terminar, cria um único atalho na área de trabalho para abrir o aplicativo; os arquivos necessários permanecem na pasta de instalação. Não exige .NET instalado nem privilégios de administrador na máquina de destino; exige conexão com a internet durante a instalação. A pasta de destino da instalação precisa ainda não existir.

Fontes usadas pelo instalador: [releases oficiais do yt-dlp](https://github.com/yt-dlp/yt-dlp/releases), [releases oficiais do Deno](https://github.com/denoland/deno/releases) e [builds Windows Essentials do FFmpeg](https://www.gyan.dev/ffmpeg/builds/), linkadas pela página oficial de downloads do [FFmpeg](https://ffmpeg.org/download.html). As versões são consultadas na instalação, sem atualizações silenciosas posteriores.

## Compilar e executar para desenvolvimento

Requer o SDK .NET 10 no computador de desenvolvimento:

```powershell
dotnet run --project .\MP3LocalDownloader.csproj
```

Ao executar a partir do projeto, o aplicativo procura as quatro ferramentas na pasta `tools` da raiz do repositório e, se ela estiver incompleta, usa o pacote portátil já extraído em `%LOCALAPPDATA%\MP3LocalDownloader\portable`. Em uma máquina de desenvolvimento sem esse cache, gere/execute o EXE portátil ou configure as quatro ferramentas em `tools`. O EXE portátil publicado inclui as ferramentas e as prepara automaticamente na primeira execução.

## Usar

1. Cole um link HTTP/HTTPS de mídia cujo download seja permitido ou clique em **Colar link** para usar o texto copiado.
2. Escolha uma pasta existente com permissão de gravação.
3. Selecione **MP3** ou **MP4** e escolha o bitrate do áudio ou a resolução máxima do vídeo.
4. Clique em **Baixar**. Para uma playlist ou mix do YouTube, todos os itens disponíveis são baixados sequencialmente para uma pasta com o nome da lista. Links individuais vão para a pasta `Downloads` dentro do destino.
5. A barra de status mostra a etapa atual, o tempo decorrido e, quando a origem fornece essa informação, o item atual e o total da playlist. Depois consulte **Downloads feitos** ou **Downloads não concluídos**; nesta última aba, cada falha aparece com um motivo resumido.

Opções de qualidade:

- **MP3:** 64, 96, 128, 160, 192, 224, 256 ou 320 kbps; padrão 192 kbps. O valor é a qualidade de codificação alvo do áudio.
- **MP4:** 320p, 720p, 1080p ou 4K (2160p); padrão 720p. A resolução escolhida é o limite máximo: vídeos menores não são ampliados. O vídeo é convertido para H.264 e o áudio para AAC.

Arquivos existentes não são substituídos. Temporários de uma operação são guardados em pasta isolada no destino e removidos ao final, inclusive em caso de cancelamento.

As preferências de destino e bitrate, o histórico de sucessos e o registro de falhas ficam localmente em `%LOCALAPPDATA%\MP3LocalDownloader`. O aplicativo inicia sempre em MP3; o formato pode ser alterado para cada operação. Cada histórico mantém até 2.000 registros; nenhum é enviado a um servidor.

## Testes locais

Com `ffmpeg.exe` e `ffprobe.exe` em `tools`, execute:

```powershell
dotnet run --project .\Tests\MP3LocalDownloader.SmokeTests.csproj -c Debug
```

O smoke test cria mídia de teste local, simula uma playlist com dois itens e verifica progresso e contagem da playlist, bitrate MP3, limite de resolução MP4, H.264, organização por pasta, histórico de sucessos e falhas, proteção contra sobrescrita, resultado parcial, erro e cancelamento. O modo `--check-package` do instalador valida que o pacote autocontido do aplicativo foi embutido.

Para criar o instalador Windows x64, abra o PowerShell na pasta do projeto e execute:

```powershell
.\scripts\build-installer.ps1
```

Entregue `artifacts\installer\LumaVideoDownloader-Setup.exe`. Esse único instalador inclui o aplicativo e baixa/valida as ferramentas na instalação; o computador de destino precisa estar conectado à internet durante esse processo. Se preferir entregar um único arquivo que já incorpora as ferramentas e não exige executar instalador, use `.\scripts\build-portable.ps1` e envie `artifacts\portable\LumaVideoDownloader.exe`.

## Segurança, licenças e limites

- O aplicativo não contorna DRM, autenticação, cookies, conteúdo privado ou restrições da origem. Use somente conteúdo que você tem autorização para baixar.
- URLs são passadas ao processo por argumentos separados, sem shell.
- O instalador para Windows x64 baixa componentes de terceiros e valida hashes antes de extraí-los. Consulte [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) no projeto e a cópia instalada junto ao aplicativo antes de redistribuir os componentes.
- A distribuição Essentials do FFmpeg indicada pelo mantenedor é GPLv3 e contém bibliotecas como libx264 e libmp3lame. Verifique e cumpra os termos de cada componente ao compartilhar o programa ou seus arquivos.
- Compatibilidade e formatos disponíveis dependem da origem e da versão atual das ferramentas. Uma falha de acesso não provoca tentativas de contornar restrições.

## Especificação

O escopo funcional, as decisões e os critérios de aceite estão em [PLANO_E_ESPECIFICACAO_MP3_LOCAL.md](PLANO_E_ESPECIFICACAO_MP3_LOCAL.md).
