# Zink VPN Setup

Zink VPN uses the Windows VPN profile named `Zink VPN`. The app button connects and disconnects that profile.

## 1. Create the VPN server

Run this on a Windows Server VPS or physical Windows Server with Administrator PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Setup-ZinkVpnServer.ps1
```

If the server is behind a router or cloud NAT:

```powershell
.\Setup-ZinkVpnServer.ps1 -ServerBehindNat
```

Save the pre-shared key printed at the end. You also need the VPN username and password.

## 2. Open network ports

Allow/forward these to the Windows Server:

- UDP `500`
- UDP `4500`
- UDP `1701`
- ESP / protocol `50`

## 3. Create the Windows client profile

Run this on the PC where Zink is installed:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Install-ZinkVpnClientProfile.ps1 -ServerAddress "YOUR_SERVER_PUBLIC_IP_OR_DNS" -PresharedKey "YOUR_PRESHARED_KEY"
```

Then open Zink, go to `Zink VPN`, and press `Connect`.

## Notes

- This starter setup uses L2TP/IPsec with MS-CHAPv2 because it can be created without buying and installing a public TLS certificate.
- For a production VPN, prefer IKEv2 or SSTP with certificates and lock down access to only the users you trust.
- If the server is in Azure, AWS, another cloud, or behind your home router, the cloud security group/router firewall must also allow the ports above.

## Free Provider Option

If you do not want to run your own Windows Server, you can create a `Zink VPN` profile that points at hide.me's free IKEv2 endpoint:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Install-ZinkVpnFreeHideMeProfile.ps1
```

The first connection may ask for your hide.me free account username/password in Windows. Once Windows saves those credentials, the Zink `Connect` button can use the profile.

If IKEv2 fails with a port/remote-computer error, create the SSTP version instead:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Install-ZinkVpnFreeHideMeSstpProfile.ps1
```

SSTP uses TCP `443`, so it is often less likely to be blocked by a router, firewall, or ISP.
