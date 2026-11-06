docker compose up -d

export ASPNETCORE_ENVIRONMENT=Development 
export PATH="$PATH:$HOME/.dotnet/tools"

dotnet build

dotnet tool install --global dotnet-ef --version 8.*
dotnet ef migrations add Inicial -p src/Fudis.Api
dotnet ef database update -p src/Fudis.Api

dotnet run --project src/Fudis.Api

dotnet test



## 1. Hallazgos (ordenados por impacto)

1. **Condición de carrera:** leer, comprobar y escribir no es atómico. Cuando dos notificaciones llegan al mismo tiempo, ambas leen el pedido como no pagado, creando doble ganancia.
2. **La notificación al restaurante está fuera de la transacción.** Si el proceso falla después de guardar y antes de notificar, el pedido queda pagado y el restaurante nunca se entera.
3. **Sin idempotencia real.** No hay nada en la base de datos que impida dos ganancias para el mismo pedido. Hoy solo "funciona" porque nadie ha probado concurrencia.
4. **`DriverId` puede ser null.** Si el pedido aún no tiene repartidor asignado, se crea una ganancia sin dueño.

Los hallazgos 1 y 3 son dos caras del mismo problema: el 1 es el mecanismo del fallo y el 3 es la garantía que falta en la base de datos. Las pruebas funcionales previas no los detectaron porque solo cubrían el caso feliz y duplicados secuenciales.

## 2. Solución propuesta

1. **Tabla de pagos con clave única.** Se guarda cada notificación con el `TransactionId` de la pasarela y un índice único. Si llega duplicada, el INSERT falla y se sabe que ya se procesó. Esa es la idempotencia a nivel de base de datos, la única garantía real bajo concurrencia.
2. **Outbox.** Se registra la notificación en una tabla (`OutboxMessages`) y luego un worker lee la fila y notifica al restaurante.
3. **Garantías:**

| Pieza | Garantía |
|---|---|
| Transacción (un único `SaveChanges`) | Pago, estado, ganancia y mensaje quedan consistentes entre sí |
| Índices únicos / `RowVersion` | Dos peticiones no procesan el mismo pago |
| Outbox + worker | La notificación externa no se pierde |

### Cambios de persistencia

- `PaymentTransaction`: nueva tabla con índice único en `TransactionId`.
- `DriverEarning`: índice único en `OrderId`.
- `Order`: `RowVersion` (token de concurrencia), `Status` como enum (guardado como texto) y `DriverId` nullable.
- `OutboxMessage`: nueva tabla (`Type`, `Payload`, `CreatedAt`, `ProcessedAt`, `Attempts`).
- Importes con `decimal(18,2)`.
- Migración: `src/Fudis.Api/Migrations/`.

### Decisiones

- La regla de negocio vive en el dominio (`Order.MarkAsPaid()`): estados válidos, ganancia del 70 % de la tarifa de envío y qué pasa si no hay repartidor.
- La base de datos decide quién gana la carrera. Quien pierde interpreta el error: si el pago ya existe, es un duplicado idempotente (200); si no, es un conflicto real (409).
- Redondeo de la ganancia: 70 % de `DeliveryFee`, truncado a 2 decimales (nunca se paga de más).

## 3. Contrato de entrada

`POST /confirm-payment` · `Content-Type: application/json`

```json
{
  "orderId": 123,
  "transactionId": "txn_8f3a9c2b",
  "amount": 15.00,
  "currency": "USD"
}
```

| Campo | Tipo | Obligatorio | Descripción |
|---|---|---|---|
| `orderId` | int | Sí | Pedido que se pagó |
| `transactionId` | string (máx. 100) | Sí | Identificador único del pago en la pasarela. Clave de idempotencia |
| `amount` | decimal (> 0) | Sí | Monto cobrado |
| `currency` | string (3 letras) | Sí | Moneda (ej. `USD`) |


## 4. Qué sucede si la pasarela envía múltiples notificaciones del mismo pago

- **En el código original**, si son enviadas al mismo tiempo, se va a registrar varias veces la ganancia, ya que ambas llamadas al endpoint leen el estado como no pagado.
- **En la versión mejorada**, el resultado es el mismo con una o con veinte notificaciones: una sola ganancia, un solo cambio de estado y un solo mensaje de outbox. Una petición gana, y las demás reciben `200` sin efectos. Las duplicadas secuenciales se detectan al consultar el `TransactionId`. Las simultáneas las frena el índice único (o `RowVersion`), y la petición perdedora comprueba que el pago ya existe y responde `200`.

## 5. Consistencia entre pago, ganancia y notificación

- Lo que pertenece a la base de datos se confirma de forma atómica. Lo que pertenece a un sistema externo se entrega después, de forma asíncrona y con reintentos.
- El registro del pago, el cambio del pedido a `Paid` y la ganancia del repartidor se guardan en una sola transacción (un único `SaveChanges`): o se confirman las tres, o no se guarda ninguna. No puede quedar un pedido pagado sin su ganancia, ni una ganancia sin pedido pagado (atomicidad).
- La transacción garantiza que todo se guarda junto, pero no impide que dos peticiones lo intenten a la vez. Eso lo hacen las restricciones de la base de datos:
  - `TransactionId` en `PaymentTransaction`: solo una petición puede registrar un pago determinado.
  - `RowVersion` en `Order`: si otra petición modificó el pedido entre mi lectura y mi escritura, la mía falla.
  - `OrderId` en `DriverEarning`: evita ganancias duplicadas.
- La notificación al restaurante no puede formar parte de la transacción (es una llamada externa). Por eso el endpoint no notifica: inserta un `OutboxMessage` en la misma transacción que el pago, y un worker en segundo plano lo entrega y lo marca como procesado, reintentando si falla. Si el pago se confirma, el aviso queda registrado de forma permanente, aunque la API se caiga justo después.

## 6. Supuestos

1. La pasarela envía `transactionId`, `amount` y `currency` además de `orderId`. El `transactionId` es único por pago y **idéntico en los reintentos**. Sin él, solo se podría deduplicar por pedido y un segundo pago legítimo se confundiría con un duplicado.
2. Si el pedido no tiene repartidor al pagarse, queda `Paid` **sin** ganancia (el cliente ya pagó, no se rechaza).
3. Un pedido cancelado o ya pagado no acepta otro pago (`409`).
4. La notificación al restaurante está **simulada** (`LoggingNotificationService` escribe en el log). La interfaz `INotificationService` permite sustituirla por una real.
5. El enunciado no incluye esquema ni contrato de la pasarela: los campos del contrato y las tablas nuevas son propuesta propia.


## 8. Limitaciones y trabajo pendiente

- **Autenticación de la pasarela (HMAC): no implementada.** Sin ella, cualquiera que conozca la URL puede marcar pedidos como pagados. Diseño: validar un encabezado `X-Signature` (HMAC del cuerpo con un secreto compartido) antes de procesar y responder `401` si no coincide.
- **Monto y moneda no se validan** contra el pedido. Consecuencia: un pedido podría marcarse pagado con un monto distinto al esperado.
- **Ganancia con repartidor asignado después del pago: no implementada.** Diseño: al asignar el repartidor a un pedido `Paid` sin ganancia, crearla en la misma transacción. El índice único sobre `OrderId` evita duplicados.
- **Entrega "al menos una vez":** si el worker notifica pero se cae antes de marcar el mensaje, el restaurante recibirá el aviso repetido. El receptor debe ser idempotente por `OrderId`.
- **Varias instancias del worker:** podrían tomar el mismo mensaje. Pendiente: reclamo por fila (`UPDLOCK, READPAST`).
- Sin reintento con espera creciente ni cola de mensajes fallidos tras agotar los intentos. El worker asume un único tipo de mensaje.
- Un `transactionId` repetido sobre otro pedido devuelve `200` sin efectos.
- Consistencia **eventual** con el restaurante: entre el pago y el aviso hay un retraso de unos segundos.
- Las migraciones se aplican a mano (`dotnet ef database update`), no al arrancar.

## 9. Herramientas de IA

- **Herramienta:** Claude (asistente de IA).
- **Para qué la usé:** [ej. discutir los problemas del código original, generar documentacion, generar el esqueleto del código y de las pruebas, resolver errores de entorno].
