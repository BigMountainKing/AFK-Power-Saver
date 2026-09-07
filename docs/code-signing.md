# AFK Power Saver code signing

Public distribution requires an Authenticode code-signing certificate whose private key is available through the Windows certificate store. A self-signed certificate is useful only for controlled development machines and must not be used for the public installer.

## Certificate preparation

1. Obtain an OV code-signing certificate, or use another public-trust signing service.
2. Complete the provider's identity validation and configure its hardware token or cloud key provider.
3. Confirm that the certificate appears under `Cert:\CurrentUser\My` or `Cert:\LocalMachine\My`, has a private key, and includes the Code Signing enhanced key usage.
4. Obtain the provider's RFC 3161 timestamp URL.

Never put a certificate password, PFX file, token PIN, private key, or cloud credential in this repository.

## Build a required signed release

From the repository root:

```powershell
.\packaging\Build-Release.ps1 `
    -RequireSigning `
    -SigningCertificateThumbprint 'YOUR_40_CHARACTER_THUMBPRINT' `
    -SigningCertificateStore CurrentUser `
    -TimestampUrl 'https://YOUR-PROVIDER-RFC3161-ENDPOINT'
```

The pipeline signs and verifies every shipped `.exe` and `.dll`, creates the installer from that signed payload, signs and verifies the outer installer, and only then runs the packaged smoke and install/uninstall tests. `-RequireSigning` prevents accidentally producing an unsigned release.

The thumbprint and timestamp URL can alternatively be supplied through `AFKPOWERSAVER_SIGNING_CERT_THUMBPRINT` and `AFKPOWERSAVER_TIMESTAMP_URL`. The private key remains in the certificate provider's protected key store.

## Independent verification

```powershell
Get-AuthenticodeSignature .\artifacts\AFK-Power-Saver-1.24.0-Setup.exe | Format-List
```

The expected status is `Valid`, with the intended publisher identity and a timestamp certificate. Keep signing with the same verified publisher identity so reputation can accumulate across releases.
