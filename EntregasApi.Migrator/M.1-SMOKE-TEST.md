# M.1 — Smoke test post-migracion

Una vez que la app de Regi Bazar esta apuntando a `prod-cutover` (cambio en `appsettings.json`
+ restart), corre este script para validar que los endpoints criticos funcionan contra la
base multi-tenant nueva.

## Como correrlo

### PowerShell (recomendado, Windows)

```powershell
cd C:\Codigos\sellgeneral-api\EntregasApi.Migrator
powershell -ExecutionPolicy Bypass -File smoke-test.ps1
```

Por default prueba contra `http://localhost:5050`. Para otro endpoint:

```powershell
powershell -ExecutionPolicy Bypass -File smoke-test.ps1 -ApiBaseUrl "https://api.regibazar.com"
```

### Incluir la parte autenticada (login + endpoints privados)

Los pasos 5-7 (login, business/me, orders/paged) necesitan la password real del Owner.
Pasala por env vars para que no quede en el historial de la shell:

```powershell
$env:SMOKE_OWNER_EMAIL = "yazmin_vara@hotmail.com"
$env:SMOKE_OWNER_PASSWORD = "la-password-real-de-regi"
powershell -ExecutionPolicy Bypass -File smoke-test.ps1
```

## Que valida

| # | Test | Esperado |
|---|---|---|
| 1 | `GET /api/pedido/{accessToken}` (publico) | 200 con order data, items, total |
| 2 | `GET /api/driver/{driverToken}` (publico) | 200 con route + deliveries |
| 3 | `GET /api/public-tanda/{token}` (publico) | 200 con tanda data |
| 4 | `GET /api/pedido/token-invalido` | 404 (no 500) |
| 5 | `POST /api/auth/login` | 200 + JWT (requiere SMOKE_OWNER_*) |
| 6 | `GET /api/business/me` con JWT | 200, BusinessId=1, Name=Regi Bazar |
| 7 | `GET /api/orders/paged` con JWT | 200, total >= 700 |

## Codigo de salida

- `0` = todo PASS (o SKIP para los autenticados)
- `1` = algun FAIL

## Interpretacion

| Salida | Significado | Accion |
|---|---|---|
| 4 PASS, 0 FAIL, 2 SKIP | Los endpoints publicos funcionan (links de clientas/repartidores/tandas). Login no se probo. | Si la app la usa Regi sin panel admin, ya esta OK. Si usas panel, correr con SMOKE_OWNER_*. |
| 6-7 PASS, 0 FAIL | Todo funciona, incluida autenticacion. | OK para dar por terminada la migracion. |
| 1+ FAIL | Algun endpoint fallo. | El detalle dice que. Investigar antes de dar por terminada. |

## Datos de prueba que usa el script (por default)

| Token | Tipo | Valor |
|---|---|---|
| Order | Id 808 (ultimo) | `f11f432173ea2c9922ae4166c69c8b5fce4133f58c2900642c903ac7f58baaf7` |
| Driver | Route Id 85 (ultimo) | `280cbd56d1733f74bd83c7220e960948a3717bc75bcfa4e114f867f7b3fa038c` |
| Tanda | ultima tanda con token | `a055b9ec1e2149598f10eff9c7376f3407533309b1ba4db88a4f7623b172538e` |

Si queres probar con otros tokens, pasalos como parametros:

```powershell
powershell -ExecutionPolicy Bypass -File smoke-test.ps1 `
    -OrderToken "otro-token-real-de-pedido" `
    -DriverToken "otro-driver-token" `
    -TandaToken "otro-tanda-token"
```

## Si todo OK

1. Notifica a Regi: "ya esta, puedes usar la app normal"
2. Deja la base vieja (ep-steep-bar-...) accesible en read-only por 30 dias como red de seguridad
3. Limpia los archivos de snapshot (`EntregasApi.Migrator/.snapshot_*.txt`) cuando estes seguro
4. **MUY IMPORTANTE**: agregá `connectionStrings.txt` al `.gitignore` o movelo fuera del repo. Contiene passwords de las 3 bases de Neon.

## Smoke test manual en el navegador

Ademas del script, abrir en el navegador:

1. `https://regibazar.com/admin/login` -> entrar con el email/password del Owner
2. Ver la lista de pedidos recientes -> deberian verse los 743
3. Click en un pedido -> deberia abrir el detalle
4. Click en un cliente -> deberia abrir la ficha
5. (Opcional) abrir un link de tracking publico en modo incognito:
   - `https://regibazar.com/pedido/{accessToken-de-algun-pedido}`
   - Debe verse el mapa y el estado del pedido
