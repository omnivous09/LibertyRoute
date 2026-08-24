# LibertyRoute Desktop — Phase 1 Safe Foundation

This package is the first implementation milestone for the Windows 10/11 LibertyRoute desktop VPN architecture.

## What is implemented

- .NET 10 Windows solution
- WPF desktop UI
- privileged-capable .NET Worker Service designed to run as a Windows Service
- constrained local Named Pipe command boundary (`STATUS`, `CONNECT`, `DISCONNECT`)
- transaction-style network snapshot before mutation
- durable recovery journal under `%ProgramData%\LibertyRoute\transactions\active.lrj`
- checksum validation of the recovery journal
- atomic/write-through journal writes
- startup detection and rollback of unfinished sessions
- exclusive in-process network transaction lock
- read-only network adapter/gateway/DNS state capture
- WireGuard engine abstraction using the official Windows embeddable-service boundary as the intended Phase 2 integration

## Deliberately NOT implemented yet

This Phase 1 build does **not** alter DNS, routes, proxy configuration, firewall state, or create a WireGuard interface. The Connect command stops after proving that the rollback snapshot has been durably committed.

That is intentional. The project requirement says rollback safety is the most important requirement. Privileged mutations should not be introduced before transaction persistence/recovery can be exercised on a real Windows 10/11 machine.

## Build

Install the current .NET SDK on Windows and then:

```powershell
dotnet restore .\LibertyRoute.sln
dotnet build .\LibertyRoute.sln -c Release
```

## Run during development

Start an elevated PowerShell for the service:

```powershell
dotnet run --project .\src\LibertyRoute.Service
```

Then in a normal PowerShell:

```powershell
dotnet run --project .\src\LibertyRoute.Desktop
```

Pressing CONNECT currently:
1. captures network adapter/gateway/DNS state;
2. writes the rollback journal durably;
3. reports `SnapshotCommitted`;
4. makes no privileged networking changes.

Press ROLL BACK to verify/clear the transaction.

## Install the service

Publish:

```powershell
dotnet publish .\src\LibertyRoute.Service -c Release -r win-x64 --self-contained true
```

Then, from Administrator PowerShell, point `binPath` to the published executable:

```powershell
sc.exe create LibertyRouteNetwork binPath= "C:\Path\LibertyRoute.Service.exe" start= auto
sc.exe description LibertyRouteNetwork "LibertyRoute privileged network and VPN recovery service"
sc.exe failure LibertyRouteNetwork reset= 86400 actions= restart/5000/restart/15000/restart/60000
sc.exe start LibertyRouteNetwork
```

## Phase 2

1. Add exact Windows proxy/PAC capture with supported WinINet/WinHTTP APIs where applicable.
2. Add native route capture/ownership using IP Helper API (`GetIpForwardTable2`, `CreateIpForwardEntry2`, `DeleteIpForwardEntry2`).
3. Add exact DNS capture/apply/restore using supported interface DNS APIs.
4. Integrate WireGuard's official Windows `embeddable-dll-service`.
5. Record every network mutation in the ownership ledger.
6. Implement connect -> verify exit IP -> connected.
7. Add forced-crash tests that prove next-launch recovery.
8. Only then add kill switch / WFP rules.

## Security notes

- The transaction journal contains network-state metadata, not VPN private keys.
- Do not put WireGuard private keys or proxy credentials in this journal.
- Phase 2 should store sensitive material with DPAPI/Windows Credential Manager.
- The Named Pipe currently has a minimal command surface; production builds should add explicit ACLs and caller identity validation before exposing privileged mutation commands.
