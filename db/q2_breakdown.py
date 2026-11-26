"""Where the remaining time of Q2 (FEFO, db/benchmark.sql) goes after db/optimize.sql.

Times each join of the query on its own, wrapped in count(*) like the benchmark, median of 7 runs. Run on a database
built with schema.sql, security.sql, seed.sql, volume.sql and optimize.sql (references/SEGURANCA_DADOS.md, section 5):

    python3 db/q2_breakdown.py /tmp/erp.db
"""
import sqlite3
import statistics
import sys
import time

db = sqlite3.connect(sys.argv[1])
W = "lt.expires_on BETWEEN '2026-10-27' AND date('2026-10-27', '+30 days')"
variants = {
 "1 faixa no índice de lotes": f"SELECT lt.id FROM lots lt WHERE {W}",
 "2 + saldos (lot_id)": f"SELECT lt.id, b.quantity FROM lots lt JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0 WHERE {W}",
 "3 + produtos (PK)": f"SELECT p.sku, b.quantity FROM lots lt JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0 JOIN products p ON p.id = lt.product_id WHERE {W}",
 "4 + endereços (PK), sem ORDER BY": f"SELECT lt.expires_on, p.sku, lt.lot_code, l.code, b.quantity FROM lots lt JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0 JOIN products p ON p.id = lt.product_id JOIN locations l ON l.id = b.location_id WHERE {W}",
 "5 Q2 completa (ORDER BY expires_on, sku)": f"SELECT lt.expires_on, p.sku, lt.lot_code, l.code, b.quantity FROM lots lt JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0 JOIN products p ON p.id = lt.product_id JOIN locations l ON l.id = b.location_id WHERE {W} ORDER BY lt.expires_on, p.sku",
 "6 ORDER BY expires_on, product_id": f"SELECT lt.expires_on, p.sku, lt.lot_code, l.code, b.quantity FROM lots lt JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0 JOIN products p ON p.id = lt.product_id JOIN locations l ON l.id = b.location_id WHERE {W} ORDER BY lt.expires_on, lt.product_id",
 "7 só a 1ª página (LIMIT 50)": f"SELECT lt.expires_on, p.sku, lt.lot_code, l.code, b.quantity FROM lots lt JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0 JOIN products p ON p.id = lt.product_id JOIN locations l ON l.id = b.location_id WHERE {W} ORDER BY lt.expires_on, lt.product_id LIMIT 50",
}
for name, sql in variants.items():
    plan = [r[3] for r in db.execute("EXPLAIN QUERY PLAN " + sql)]
    times = []
    for _ in range(7):
        t = time.perf_counter(); n = db.execute("SELECT count(*) FROM (" + sql + ")").fetchone()[0]; times.append((time.perf_counter() - t) * 1000)
    print(f"{name}|{n}|{statistics.median(times):.2f}|{' / '.join(plan)}")
print("lots in window:", db.execute(f"SELECT count(*) FROM lots lt WHERE {W}").fetchone()[0])
print("balances total:", db.execute("SELECT count(*) FROM stock_balances").fetchone()[0])
