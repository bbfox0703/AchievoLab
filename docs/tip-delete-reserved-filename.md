# Tip: Deleting Files with Reserved Names (e.g., NUL) on Windows

Windows reserves certain filenames (`NUL`, `CON`, `PRN`, `AUX`, `COM1`–`COM9`, `LPT1`–`LPT9`).
Standard tools cannot delete these files directly. Use the `\\?\` path prefix to bypass the restriction.

## Steps

1. Open `cmd.exe` with **Administrator** privilege.
2. Navigate to the directory containing the file:
   ```
   cd D:\Github\AchievoLab
   ```
3. Confirm the exact filename with `dir /x /a`.
4. Delete the file using the extended-length path prefix:
   ```
   Del \\?\D:\Github\AchievoLab\nul
   ```

## PowerShell 6+ Alternative

```powershell
Remove-Item -LiteralPath '\\?\D:\Github\AchievoLab\nul'
```
