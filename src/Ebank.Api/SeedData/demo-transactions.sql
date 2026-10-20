-- Adds 206 illustrative transactions to the current account and 117 to the
-- savings account over the last 90 days.
-- Re-running is safe: fixed transaction IDs prevent duplicate inserts, and only
-- newly inserted transaction amounts are applied to account balances.
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM "CustomerAccounts"
        WHERE "Id" = 'a94ebf7b-3239-4c16-a0cc-5f43360c17a1'::uuid
          AND "Type" = 'Current'
    ) THEN
        RAISE EXCEPTION 'The specified current account does not exist or is not a current account.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM "CustomerAccounts"
        WHERE "Id" = '2ac18289-65f3-47be-980d-e9bd7c363e8d'::uuid
          AND "Type" = 'Savings'
    ) THEN
        RAISE EXCEPTION 'The specified savings account does not exist or is not a savings account.';
    END IF;
END;
$$;

WITH seed_rows AS (
    SELECT
        md5(
            'ebank-demo-transactions-v1:' ||
            account."Id"::text || ':' ||
            sequence.number::text
        )::uuid AS "Id",
        account."Id" AS "AccountId",
        CASE
            WHEN sequence.number % 10 = 0 THEN 'SalaryReceived'
            WHEN sequence.number % 5 = 0 THEN 'AtmWithdrawal'
            ELSE 'EcommercePurchase'
        END AS "Type",
        CASE
            WHEN sequence.number % 10 = 0 THEN 'Salary - Demo employer'
            WHEN sequence.number % 5 = 0 THEN 'ATM cash withdrawal'
            ELSE (ARRAY[
                'Online purchase - Northstar Market',
                'Online purchase - Green Basket',
                'Online purchase - City Outfitters',
                'Online purchase - Paper & Pine',
                'Online purchase - Harbor Electronics'
            ])[(sequence.number % 5) + 1]
        END AS "Label",
        CASE
            WHEN sequence.number % 10 = 0
                THEN (1100 + ((sequence.number * 73) % 401))::numeric(18, 2)
            WHEN sequence.number % 5 = 0
                THEN -(20 + ((sequence.number * 37) % 130))::numeric(18, 2)
            ELSE -(12.50 + ((sequence.number * 173) % 950) / 100.0)::numeric(18, 2)
        END AS "Amount",
        account."Currency",
        now()
            - ((sequence.number * 37) % 90) * interval '1 day'
            - ((sequence.number * 223) % 86400) * interval '1 second'
            AS "OccurredAt"
    FROM "CustomerAccounts" AS account
    CROSS JOIN LATERAL generate_series(
        1,
        CASE
            WHEN account."Id" = 'a94ebf7b-3239-4c16-a0cc-5f43360c17a1'::uuid THEN 206
            ELSE 117
        END
    ) AS sequence(number)
    WHERE account."Id" IN (
        '2ac18289-65f3-47be-980d-e9bd7c363e8d'::uuid,
        'a94ebf7b-3239-4c16-a0cc-5f43360c17a1'::uuid
    )
),
inserted_rows AS (
    INSERT INTO "AccountTransactions"
        ("Id", "AccountId", "Type", "Label", "Amount", "Currency", "OccurredAt")
    SELECT
        "Id", "AccountId", "Type", "Label", "Amount", "Currency", "OccurredAt"
    FROM seed_rows
    ON CONFLICT ("Id") DO NOTHING
    RETURNING "AccountId", "Amount"
),
balance_deltas AS (
    SELECT
        "AccountId" AS account_id,
        SUM("Amount") AS amount_delta,
        COUNT(*) AS inserted_count
    FROM inserted_rows
    GROUP BY "AccountId"
),
updated_accounts AS (
    UPDATE "CustomerAccounts" AS account
    SET "Balance" = account."Balance" + delta.amount_delta
    FROM balance_deltas AS delta
    WHERE account."Id" = delta.account_id
    RETURNING account."Id" AS account_id, account."Balance" AS balance
)
SELECT
    account."Id" AS account_id,
    COUNT(seeded."Id") AS total_seeded_transactions,
    COALESCE(delta.inserted_count, 0) AS inserted_this_run,
    COALESCE(delta.amount_delta, 0) AS balance_change,
    COALESCE(updated.balance, account."Balance") AS current_balance
FROM "CustomerAccounts" AS account
CROSS JOIN LATERAL generate_series(
    1,
    CASE
        WHEN account."Id" = 'a94ebf7b-3239-4c16-a0cc-5f43360c17a1'::uuid THEN 206
        ELSE 117
    END
) AS sequence(number)
LEFT JOIN "AccountTransactions" AS seeded
    ON seeded."Id" = md5(
        'ebank-demo-transactions-v1:' ||
        account."Id"::text || ':' ||
        sequence.number::text
    )::uuid
LEFT JOIN balance_deltas AS delta ON delta.account_id = account."Id"
LEFT JOIN updated_accounts AS updated ON updated.account_id = account."Id"
WHERE account."Id" IN (
    'a94ebf7b-3239-4c16-a0cc-5f43360c17a1'::uuid,
    '2ac18289-65f3-47be-980d-e9bd7c363e8d'::uuid
)
GROUP BY
    account."Id",
    account."Balance",
    delta.inserted_count,
    delta.amount_delta,
    updated.balance
ORDER BY account."Id";

COMMIT;
