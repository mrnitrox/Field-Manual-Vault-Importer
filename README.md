# Field Manual Vault Importer

A one-window Windows program that downloads the public-domain military publications of the
[T.REX ARMS Field Manual Vault](https://www.trex-arms.com/field-manual-vault) to a folder of your choice:

```
<folder>\Field Manual Vault\<Branch>\<Type>\<Designation> <Title> (<Year>).pdf / .epub
```

## Using it

1. Choose the destination folder, the collections (Current Doctrine, Recent Archive, Lessons Learned, Historic)
   and the file types (PDF, EPUB or both).
2. Start. The program first reads the catalog (one page a second), then downloads the files one at a time.
3. Pause / Resume and Cancel at any time. After a crash or a power cut, choosing the same folder offers to resume:
   finished files are kept, a partial file continues from where it stopped.

Progress is kept in `vault-download.json` in the `Field Manual Vault` folder, so it travels with the files.

## Cloudflare checks

If the site asks for a browser check, the run pauses and **Open the site to verify** appears. Complete the check in
the built-in browser (Microsoft Edge WebView2, part of Windows 10/11); the run then continues through that browser,
more slowly. The program never answers a check itself. Three checks within an hour mean the site wants less traffic:
continue later.

## Building

.NET 8 SDK, Windows:

```
dotnet publish src/FieldManualVaultImporter -c Release -o publish
```

gives `publish\FieldManualVaultImporter.exe`, a single self-contained file (no .NET needed where it runs).

The site's pages are read as a visitor reads them: robots.txt allows them and disallows the data endpoints
(`/api/`, `__data.json`), which aren't used.

## License

[MIT](LICENSE). The exe also contains the .NET runtime and the WebView2 SDK, under their own licenses: see
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Not affiliated with T.REX ARMS. The publications are U.S. government works in the public domain.
