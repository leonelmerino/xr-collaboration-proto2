# Configuración de red del laboratorio

**Proyecto:** XR Collaboration Prototype 2 · ANID Proyectos de Exploración 13250116
**Fecha:** 2026-08-27
**Alcance:** router ASUS de la intranet, prioridad de rutas en los tres PC, y apertura de puertos para que cualquiera de los tres pueda actuar como Host.

Este documento resuelve tres problemas abiertos:

1. Las reservas de IP fija por MAC del router **no siempre se respetan** (Bug 13 de [`bug_dev.md`](bug_dev.md)).
2. Los PC tienen **dos interfaces activas** —WiFi de la universidad y cable a la intranet— y hay que garantizar que internet salga por WiFi y el tráfico de la aplicación VR vaya por cable.
3. Hoy **solo Stimulus 1 puede actuar como Host**; los otros dos no tienen los puertos abiertos.

## Equipamiento y direccionamiento

| Elemento | Valor |
| --- | --- |
| Router | ASUS ROG Rapture GT-AXE16000 |
| Administración | `http://192.168.88.1` |
| Subred de la intranet | `192.168.88.0/24` |
| WiFi de internet | UC invitados (cuentas `stimulusuno`, `stimulusdos`, `stimulustres`) |

Las IP de las estaciones se indican como `192.168.88.11`–`.13` a lo largo del documento. Ajustar si las reservas actuales usan otros valores.

---

## Arquitectura objetivo

| Interfaz | Red | Para qué | Salida a internet |
| --- | --- | --- | --- |
| WiFi | UC invitados | Navegación, `git pull`, login de Unity | **Sí** |
| Ethernet | Intranet (`192.168.88.0/24`) | Tráfico de la aplicación VR (UDP 7777 / 7778) | No |

Dos medidas complementarias ordenan el tráfico. **Hacen falta las dos** —cubren problemas distintos, y ninguna reemplaza a la otra:

| Medida | Qué resuelve | Qué NO resuelve |
| --- | --- | --- |
| Quitar la puerta de enlace de Ethernet (B.4) | El enrutamiento: sin gateway solo queda una ruta por omisión, la de WiFi | El orden de consulta de DNS; y se revierte si el DHCP la vuelve a asignar |
| Fijar métricas de interfaz (B.3) | El orden de DNS, y actúa de respaldo permanente si el gateway reaparece | Por sí sola no elimina la ruta por omisión duplicada |

### El tráfico de la aplicación no depende de ninguna de las dos

Conviene saberlo porque acota el riesgo de estos cambios: el tráfico entre estaciones sale por el cable **siempre**, con o sin métricas, porque la ruta conectada `192.168.88.0/24` es más específica que cualquier ruta por omisión y Windows resuelve primero por prefijo más largo.

El LAN discovery también es robusto por diseño. `LanDiscoveryService` enumera todas las NIC activas no virtuales y envía un broadcast dirigido a la subred de cada una —`192.168.88.255` en el caso del cable—, que por la misma regla de prefijo sale por Ethernet. Adicionalmente envía un broadcast limitado a `255.255.255.255`, que sí sale por la NIC por omisión del sistema, pero es solo un respaldo. Del lado del cliente, el socket escucha en `IPAddress.Any`, así que recibe por cualquier interfaz.

En otras palabras: **estas dos medidas son para que internet funcione bien, no para que la aplicación funcione.**

---

## Parte A — Router: reservas de IP por MAC

> **Ya descartado:** las reservas están registradas contra la MAC del adaptador **Ethernet**, no la del WiFi. Esa causa —la más habitual— no aplica aquí. Las secciones siguientes cubren las que quedan.

### A.1 Acceder a la interfaz de administración

1. Conectar el PC al router por cable.
2. Abrir el navegador en `http://192.168.88.1` (o `http://router.asus.com`).
3. Iniciar sesión con las credenciales de administrador del laboratorio.

El GT-AXE16000 usa la interfaz ASUSWRT con el tema ROG. Los menús son los mismos de ASUSWRT, con distinta presentación visual.

### A.2 Causa más probable — reservas dentro del pool dinámico

Si una IP reservada cae dentro del rango que el router reparte por su cuenta, otro dispositivo puede tomarla primero y la reserva queda sin efecto. Con las MAC ya verificadas, **esta es la primera candidata a revisar.**

Ir a **LAN → DHCP Server** y comparar dos cosas:

1. **IP Pool Starting Address** / **IP Pool Ending Address** — el rango dinámico.
2. Las IP de la lista de asignación manual, más abajo en la misma pantalla.

Las reservas deben quedar **fuera** del pool:

| Concepto | Valor sugerido |
| --- | --- |
| Pool dinámico | `192.168.88.100` – `192.168.88.200` |
| Reservas de las estaciones | `192.168.88.11` – `192.168.88.13` |

Si hoy el pool arranca en `.2` o `.10`, ahí está el conflicto: subir el inicio del pool a `.100` y dejar la parte baja del rango libre para las reservas.

### A.3 Revisar la lista de asignación manual

En **LAN → DHCP Server → Manually Assigned IP around the DHCP list**:

1. **Enable Manual Assignment** debe estar en `Yes`.
2. Debe haber exactamente una entrada por equipo, con la MAC Ethernet y la IP correspondiente.
3. Verificar que no haya **entradas duplicadas o huérfanas** de configuraciones anteriores —por ejemplo la MAC del WiFi de algún equipo apuntando a una IP del rango de las estaciones—. Una entrada vieja puede estar reclamando la IP que debería recibir otra máquina.
4. Aplicar y guardar. Confirmar que la lista quedó guardada volviendo a cargar la página: en algunas versiones de firmware la lista se pierde si se aplica junto con otros cambios.

### A.4 Descartar la aleatorización de MAC en Windows

Aunque las reservas usen la MAC correcta, Windows puede presentar una MAC aleatoria y anular la coincidencia. Es poco frecuente en Ethernet, pero se descarta en un minuto:

**Configuración → Red e Internet → Ethernet → Direcciones de hardware aleatorias → Desactivado**

Para verificar que la MAC anunciada es la física, comparar en cada PC:

```powershell
Get-NetAdapter -Name "Ethernet" | Format-Table Name, MacAddress, LinkSpeed
```

Ese valor debe coincidir exactamente con el de la reserva en el router.

### A.5 Limpiar los leases anteriores

Un lease vigente puede entregarse antes de que se aplique una reserva nueva. Después de cualquier cambio en el router:

1. Reiniciar el router (**Administration → System → Reboot**).
2. En cada PC, renovar la concesión:

```powershell
ipconfig /release
ipconfig /renew
ipconfig | Select-String -Pattern "Ethernet" -Context 0,6
```

3. Confirmar que cada equipo recibió la IP reservada.

### A.6 Si el problema persiste

Recolectar estos datos y anotarlos en [`bug_dev.md`](bug_dev.md) (Bug 13):

* ¿Falla siempre el mismo equipo o es aleatorio?
* ¿La IP entregada cae dentro o fuera del pool dinámico?
* ¿Coincide con la reserva de otro equipo?
* ¿Aparece algún dispositivo inesperado en **Network Map → Clients** que pudiera estar sirviendo DHCP?
* ¿Hay nodos AiMesh en la red? La configuración DHCP la sirve solo el router principal, pero conviene registrarlo.

> **Solución definitiva recomendada.** Con las MAC ya verificadas y el problema aún presente, lo más eficiente para un laboratorio de equipos fijos es **dejar de depender del DHCP**: configurar IP estática en cada PC (Parte B.4). El router queda solo como switch para este tráfico, y el bug deja de ser relevante. Las reservas pueden mantenerse como respaldo, pero ya no serían el mecanismo del que depende la sesión.

---

## Parte B — Prioridad de rutas en los PC

Ejecutar en **los tres equipos**, en PowerShell **como administrador**.

### B.1 Entender el criterio

Windows elige la interfaz por la **ruta más específica**, y solo usa la métrica para desempatar entre rutas del mismo prefijo:

* El tráfico a `192.168.88.x` coincide con la **ruta conectada** de Ethernet (`/24`), más específica que cualquier ruta por omisión. **Sale por cable siempre, sin importar la métrica.**
* El tráfico a internet coincide únicamente con la **ruta por omisión** (`0.0.0.0/0`). Si ambas interfaces publican una, gana la de menor métrica.

Por eso subir la métrica de Ethernet no afecta a la aplicación VR: solo cambia por dónde sale internet.

### B.2 Revisar el estado actual

```powershell
Get-NetIPInterface -AddressFamily IPv4 |
    Sort-Object InterfaceMetric |
    Format-Table ifIndex, InterfaceAlias, InterfaceMetric, ConnectionState
```

### B.3 Fijar las métricas

Menor número = mayor prioridad. WiFi debe quedar por debajo:

```powershell
Set-NetIPInterface -InterfaceAlias "Wi-Fi"    -InterfaceMetric 10
Set-NetIPInterface -InterfaceAlias "Ethernet" -InterfaceMetric 50
```

Ajustar `InterfaceAlias` si los adaptadores tienen otro nombre (verlo con `Get-NetAdapter`).

**Este paso no es redundante con B.4.** Cubre dos cosas que quitar el gateway no resuelve:

* **Orden de consulta de DNS.** Windows consulta los servidores DNS siguiendo el orden de métrica de las interfaces. Si Ethernet tiene mejor métrica y el DHCP del router se anunció como servidor DNS, Windows pregunta primero a `192.168.88.1` por nombres de internet. Según el estado de la WAN del router, eso produce resolución lenta o fallida **aunque el enrutamiento sea correcto**. Es el síntoma clásico de "hay conexión pero las páginas no cargan".
* **Permanencia.** La métrica fijada con `Set-NetIPInterface` persiste entre reinicios y reconexiones; la eliminación de la ruta por omisión (B.4, opción rápida) no.

### B.4 Quitar la puerta de enlace del cable

Elimina la ruta por omisión duplicada, que es la causa directa del problema de enrutamiento.

**Opción rápida** — efecto inmediato, pero se revierte al renovar DHCP o reiniciar:

```powershell
Remove-NetRoute -InterfaceAlias "Ethernet" -DestinationPrefix "0.0.0.0/0" -Confirm:$false
```

**Opción permanente y recomendada** — IP estática, sin gateway y sin DNS. Es además la que resuelve el Bug 13 de raíz, porque elimina la dependencia del DHCP del router.

Reemplazar la IP por la que corresponda a cada equipo:

```powershell
# Limpiar configuración previa de la interfaz
Remove-NetIPAddress -InterfaceAlias "Ethernet" -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetRoute     -InterfaceAlias "Ethernet" -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue

# Asignar IP fija SIN -DefaultGateway
# Stimulus 1 → .11    Stimulus 2 → .12    Stimulus 3 → .13
New-NetIPAddress -InterfaceAlias "Ethernet" -IPAddress 192.168.88.11 -PrefixLength 24

# Sin servidores DNS en esta interfaz: el DNS lo resuelve la WiFi
Set-DnsClientServerAddress -InterfaceAlias "Ethernet" -ResetServerAddresses
```

La última línea importa: sin servidores DNS en Ethernet, el problema de orden de consulta descrito en B.3 desaparece por completo y las métricas quedan puramente como respaldo.

### B.5 Verificar el resultado

```powershell
# ¿Por dónde sale internet? → debe indicar la interfaz Wi-Fi
Find-NetRoute -RemoteIPAddress 8.8.8.8 | Select-Object -First 1 InterfaceAlias, IPAddress

# ¿Por dónde sale el tráfico a otra estación? → debe indicar Ethernet
Find-NetRoute -RemoteIPAddress 192.168.88.12 | Select-Object -First 1 InterfaceAlias, IPAddress

# ¿Qué servidores DNS quedaron configurados? → no debe aparecer 192.168.88.1
Get-DnsClientServerAddress -AddressFamily IPv4 | Format-Table InterfaceAlias, ServerAddresses
```

Los dos primeros comandos son la verificación definitiva del enrutamiento. Si ambos devuelven la interfaz correcta y el DNS no apunta al router, la configuración de rutas está lista.

---

## Parte C — Puertos y firewall

La aplicación usa dos puertos UDP:

| Puerto | Protocolo | Función |
| --- | --- | --- |
| 7777 | UDP | Tráfico de juego (Netcode for GameObjects) |
| 7778 | UDP | LAN discovery (broadcast del Host) |

**Los tres equipos necesitan ambos puertos abiertos**, no solo Stimulus 1. Aunque hoy el rol de Host esté asignado a Stimulus 1, cualquiera debe poder asumirlo si esa máquina falla durante una sesión.

Ejecutar en **los tres equipos**, en PowerShell **como administrador**.

### C.1 Marcar la red del cable como privada

**Este paso suele ser la causa real de que solo un equipo funcione como Host.** Si Windows clasifica la red del cable como *Pública*, aplica un perfil de firewall más restrictivo y bloquea el tráfico entrante aunque las reglas existan.

```powershell
# Ver cómo está clasificada cada red
Get-NetConnectionProfile

# Marcar la del cable como privada
Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private
```

### C.2 Crear las reglas de firewall

```powershell
New-NetFirewallRule -DisplayName "Unity NGO Game Port" -Direction Inbound `
    -Protocol UDP -LocalPort 7777 -Action Allow -Profile Private

New-NetFirewallRule -DisplayName "Unity LAN Discovery" -Direction Inbound `
    -Protocol UDP -LocalPort 7778 -Action Allow -Profile Private
```

Si las reglas ya existen de una configuración anterior, el comando falla con un error de nombre duplicado. Para rehacerlas:

```powershell
Remove-NetFirewallRule -DisplayName "Unity NGO Game Port", "Unity LAN Discovery" -ErrorAction SilentlyContinue
```

### C.3 Verificar

```powershell
Get-NetFirewallRule -DisplayName "Unity*" |
    Format-Table DisplayName, Enabled, Direction, Profile, Action
```

Las dos reglas deben aparecer con `Enabled: True`, `Direction: Inbound`, `Profile: Private`, `Action: Allow`.

> **Nota:** no hacen falta reglas de salida. Windows permite el tráfico saliente por omisión.

---

## Checklist de verificación

Ejecutar en los tres equipos al terminar la configuración:

- [ ] `Get-NetAdapter` muestra Ethernet y Wi-Fi en estado `Up`
- [ ] La IP de Ethernet coincide con la asignada a esa estación
- [ ] `Find-NetRoute -RemoteIPAddress 8.8.8.8` devuelve la interfaz **Wi-Fi**
- [ ] `Find-NetRoute -RemoteIPAddress <IP de otra estación>` devuelve **Ethernet**
- [ ] `Get-DnsClientServerAddress` no muestra `192.168.88.1` como servidor DNS
- [ ] Hay navegación web funcionando, y las páginas cargan sin demora inicial
- [ ] `ping` responde desde y hacia los otros dos equipos
- [ ] `Get-NetConnectionProfile` muestra la red del cable como `Private`
- [ ] Las dos reglas de firewall existen y están habilitadas

**Prueba funcional final:** arrancar la aplicación como Host en un equipo **distinto de Stimulus 1** y confirmar que los otros dos lo encuentran por LAN discovery. Es la única verificación real de que los tres pueden hospedar.

---

## Diagnóstico de problemas

| Síntoma | Causa probable | Dónde revisar |
| --- | --- | --- |
| El equipo recibe una IP distinta de la reservada | La IP reservada cae dentro del pool dinámico, o hay una entrada duplicada en la lista manual | A.2, A.3 |
| No hay navegación web | Ethernet conserva una puerta de enlace, o quedó con mejor métrica | B.4, B.3 |
| Hay conexión pero las páginas no cargan (o tardan mucho) | El DNS se está consultando al router en vez de a la WiFi | B.3, B.4 |
| El ping entre estaciones falla | Red clasificada como pública, o cable/puerto del switch | C.1 |
| Los clientes no encuentran al Host | Falta la regla del puerto 7778, o el perfil de firewall no coincide | C.1, C.2 |
| El Host aparece pero la conexión no completa | Falta la regla del puerto 7777 | C.2 |
| El HUD dice "Host activo" pero nadie lo encuentra | Puede ser el Bug 5: `StartHost()` falló y el HUD lo reporta igual | [`bug_dev.md`](bug_dev.md) |

---

## Referencias

* [`bug_dev.md`](bug_dev.md) — Bug 13 (reservas de IP) y Bug 5 (HUD del Host)
* [`test_protocol.md`](test_protocol.md) — verificación de conectividad al inicio de cada sesión
* [`dev_log.md`](dev_log.md) — entrada del 2026-05-27, diagnóstico original de conectividad LAN
