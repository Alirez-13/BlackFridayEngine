#!/usr/bin/env bash
set -e

API_URL="${API_URL:-http://localhost:8080}"
TENANT="tenant-electronics"
SALE_ID="sale-elec-conc-$(date +%s)"
CONCURRENT_REQUESTS=150

echo "================================================================"
echo " Black Friday Concurrency Test (150 Concurrent Requests / 100 Stock)"
echo " Target: $API_URL/api/sales/$SALE_ID/reservations"
echo " Tenant: $TENANT"
echo "================================================================"

echo "[1/4] Creating fresh sale event '$SALE_ID' with 100 stock..."
START_TIME=$(date -u -d "-5 minutes" +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date -u -v-5M +"%Y-%m-%dT%H:%M:%SZ")
END_TIME=$(date -u -d "+2 hours" +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date -u -v+2H +"%Y-%m-%dT%H:%M:%SZ")

curl -s -X POST "$API_URL/api/sales" \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d "{
    \"id\": \"$SALE_ID\",
    \"skuId\": \"SKU-CONC-TEST\",
    \"productName\": \"Concurrency Test 65-Inch 4K OLED TV\",
    \"originalPrice\": 1999.99,
    \"discountedPrice\": 1299.99,
    \"initialStock\": 100,
    \"startTimeUtc\": \"$START_TIME\",
    \"endTimeUtc\": \"$END_TIME\"
  }" | grep -o '"remainingStock":[0-9]*'
echo ""

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

echo "[2/4] Firing $CONCURRENT_REQUESTS concurrent reservation requests..."
for i in $(seq 1 $CONCURRENT_REQUESTS); do
  (
    STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X POST \
      "$API_URL/api/sales/$SALE_ID/reservations" \
      -H "X-Tenant-ID: $TENANT" \
      -H "Content-Type: application/json" \
      -d "{\"customerId\": \"cust-$i\", \"quantity\": 1}")
    echo "$STATUS" > "$TMP_DIR/req_$i.txt"
  ) &
done

wait

SUCCESS_202=$(grep -l "202" "$TMP_DIR"/req_*.txt 2>/dev/null | wc -l | tr -d ' ')
CONFLICT_409=$(grep -l "409" "$TMP_DIR"/req_*.txt 2>/dev/null | wc -l | tr -d ' ')
OTHER=$((CONCURRENT_REQUESTS - SUCCESS_202 - CONFLICT_409))

echo "[3/4] Results Summary:"
echo "  -> HTTP 202 (Accepted Reservations) : $SUCCESS_202"
echo "  -> HTTP 409 (Out of Stock Rejected) : $CONFLICT_409"
echo "  -> Other HTTP Statuses              : $OTHER"

echo "[4/4] Verifying final stock and background pipeline status..."
sleep 2
FINAL_STATE=$(curl -s -H "X-Tenant-ID: $TENANT" "$API_URL/api/sales/$SALE_ID")
echo "  -> Final Sale State: $FINAL_STATE"

PIPELINE_STATE=$(curl -s -H "X-Tenant-ID: $TENANT" "$API_URL/api/pipeline/status")
echo "  -> Pipeline State  : $PIPELINE_STATE"

if [ "$SUCCESS_202" -eq 100 ] && [ "$CONFLICT_409" -eq 50 ]; then
  echo "✅ PASS: Exactly 100 items sold and 50 rejected. ZERO OVERSELLING!"
else
  echo "❌ FAIL: Unexpected reservation distribution."
  exit 1
fi