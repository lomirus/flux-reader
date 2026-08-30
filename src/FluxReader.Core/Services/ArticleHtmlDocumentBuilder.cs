using System.Net;

namespace FluxReader.Core.Services;

public static class ArticleHtmlDocumentBuilder
{
    public static string Create(
        string? content,
        Uri? baseUri,
        bool useDarkTheme,
        IReadOnlyList<WebsiteStylesheetReference>? externalStylesheets = null)
    {
        var fragment = ArticleContentParser.PrepareHtml(content, baseUri);
        if (!ArticleContentParser.ContainsHtmlMarkup(fragment))
        {
            fragment = $"<div class=\"plain-text\">{WebUtility.HtmlEncode(fragment)}</div>";
        }

        var stylesheetLinks = CreateStylesheetLinks(externalStylesheets);
        var externalStyleSources = stylesheetLinks.Length == 0 ? string.Empty : " https: http:";
        var colorScheme = useDarkTheme ? "dark" : "light";
        return $$"""
            <!doctype html>
            <html lang="">
            <head>
              <meta charset="utf-8">
              <meta http-equiv="Content-Security-Policy"
                    content="default-src 'none'; script-src 'none'; style-src 'unsafe-inline'{{externalStyleSources}}; img-src https: http: data:; media-src https: http:; font-src 'none'; connect-src 'none'; frame-src 'none'; child-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              {{stylesheetLinks}}
              <style>
                :root {
                  color-scheme: {{colorScheme}};
                  font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
                  font-size: 16px;
                  line-height: 1.625;
                  color: CanvasText;
                  background: transparent;
                }
                html, body {
                  min-height: 100%;
                  margin: 0;
                  padding: 0;
                  background: transparent;
                }
                body {
                  padding-block-end: 24px;
                  overflow-wrap: anywhere;
                }
                .plain-text {
                  white-space: pre-wrap;
                }
                h1, h2, h3, h4, h5, h6 {
                  margin-block: 1.4em 0.55em;
                  line-height: 1.25;
                }
                h1:first-child, h2:first-child, h3:first-child,
                h4:first-child, h5:first-child, h6:first-child,
                p:first-child, pre:first-child, figure:first-child {
                  margin-block-start: 0;
                }
                p, ul, ol, dl, table, blockquote, pre, figure {
                  margin-block: 0 1em;
                }
                ul, ol {
                  padding-inline-start: 1.6em;
                }
                li + li {
                  margin-block-start: 0.3em;
                }
                a {
                  color: LinkText;
                  text-underline-offset: 0.15em;
                }
                a:focus-visible {
                  outline: 2px solid AccentColor;
                  outline-offset: 2px;
                }
                blockquote {
                  margin-inline: 0;
                  padding-inline-start: 1em;
                  border-inline-start: 3px solid AccentColor;
                  color: GrayText;
                }
                code, kbd, samp, pre {
                  font-family: "Cascadia Mono", Consolas, monospace;
                }
                :not(pre) > code, kbd, samp {
                  padding: 0.12em 0.35em;
                  border-radius: 4px;
                  background: color-mix(in srgb, CanvasText 8%, Canvas);
                  font-size: 0.9em;
                }
                pre {
                  max-width: 100%;
                  box-sizing: border-box;
                  overflow-x: auto;
                  padding: 14px 16px;
                  border: 1px solid color-mix(in srgb, CanvasText 12%, Canvas);
                  border-radius: 8px;
                  background: color-mix(in srgb, CanvasText 7%, Canvas);
                  font-size: 14px;
                  line-height: 1.55;
                  white-space: pre;
                  overflow-wrap: normal;
                }
                pre code {
                  padding: 0;
                  background: transparent;
                  font-size: inherit;
                }
                img, video {
                  display: block;
                  max-width: 100%;
                  height: auto;
                  margin-inline: auto;
                  border-radius: 8px;
                }
                audio {
                  width: 100%;
                  max-width: 100%;
                }
                figure {
                  margin-inline: 0;
                }
                figcaption {
                  margin-block-start: 0.45em;
                  color: GrayText;
                  font-size: 0.9em;
                  text-align: center;
                }
                table {
                  display: block;
                  max-width: 100%;
                  overflow-x: auto;
                  border-collapse: collapse;
                }
                th, td {
                  padding: 0.45em 0.7em;
                  border: 1px solid color-mix(in srgb, CanvasText 18%, Canvas);
                  text-align: start;
                  vertical-align: top;
                }
                th {
                  background: color-mix(in srgb, CanvasText 7%, Canvas);
                  font-weight: 600;
                }
                hr {
                  margin-block: 1.5em;
                  border: 0;
                  border-block-start: 1px solid color-mix(in srgb, CanvasText 18%, Canvas);
                }
              </style>
            </head>
            <body>{{fragment}}</body>
            </html>
            """;
    }

    private static string CreateStylesheetLinks(
        IReadOnlyList<WebsiteStylesheetReference>? externalStylesheets)
    {
        if (externalStylesheets is null || externalStylesheets.Count == 0)
        {
            return string.Empty;
        }

        var links = externalStylesheets
            .Where(stylesheet =>
                stylesheet.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                stylesheet.Uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Select(stylesheet =>
            {
                var href = WebUtility.HtmlEncode(stylesheet.Uri.AbsoluteUri);
                var media = string.IsNullOrWhiteSpace(stylesheet.Media)
                    ? string.Empty
                    : $" media=\"{WebUtility.HtmlEncode(stylesheet.Media)}\"";
                return $"<link rel=\"stylesheet\" href=\"{href}\"{media}>";
            });
        return string.Join(Environment.NewLine, links);
    }
}
