# Avisos de componentes de terceiros

O Luma Video Downloader inclui ferramentas externas no executável portátil; o instalador alternativo as baixa durante a instalação. Antes de redistribuir o aplicativo ou os binários dessas ferramentas, consulte os termos atuais das versões escolhidas e cumpra as obrigações aplicáveis.

| Componente | Fonte | Licença / observação |
|---|---|---|
| yt-dlp | [GitHub Releases](https://github.com/yt-dlp/yt-dlp/releases) | Unlicense; a compilação oficial também inclui componentes com licenças próprias. |
| Deno | [GitHub Releases](https://github.com/denoland/deno/releases) | MIT. |
| FFmpeg Essentials para Windows | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) e [FFmpeg downloads](https://ffmpeg.org/download.html) | O mantenedor identifica as builds estáticas como GPLv3. Essentials inclui, entre outras bibliotecas, libx264 e libmp3lame. |
| .NET 10 | [Microsoft .NET](https://dotnet.microsoft.com/) | O runtime Windows está incluído na publicação autocontida do aplicativo e do instalador; consulte os avisos da distribuição oficial do .NET. |

O executável portátil grava uma cópia destes avisos junto às ferramentas extraídas; o instalador grava uma cópia junto ao programa. FFmpeg pode incluir bibliotecas externas além das citadas; a lista de componentes da build muda ao longo do tempo. Confirme a composição e a origem da versão incluída antes de distribuir os arquivos.
