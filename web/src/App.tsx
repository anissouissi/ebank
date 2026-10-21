import { useEffect, useRef, useState, type FormEvent } from 'react'
import keycloak, { initializeKeycloak } from './auth/keycloak'
import KycSection from './KycSection'
import './App.css'

type Customer = {
  username: string | null
  firstName: string | null
  lastName: string | null
}

type AccountType = 'Current' | 'Savings'

type CustomerAccount = {
  id: string
  type: AccountType
  accountNumber: string
  currency: string
  balance: number
  createdAt: string
}

type CustomerTransaction = {
  id: string
  accountId: string
  accountType: AccountType
  accountNumber: string
  type: 'Other' | 'EcommercePurchase' | 'AtmWithdrawal' | 'SalaryReceived' | 'MoneyTransfer'
  label: string
  amount: number
  currency: string
  occurredAt: string
}

type TransactionPage = {
  items: CustomerTransaction[]
  page: number
  pageSize: number
  totalCount: number
}

type CurrencyTotal = {
  currency: string
  amount: number
}

type OverviewSummary = {
  balances: CurrencyTotal[]
  monthlySpending: CurrencyTotal[]
  monthlyIncome: CurrencyTotal[]
  accountCount: number
  currentAccountCount: number
  savingsAccountCount: number
  recentTransactions: CustomerTransaction[]
}

type AssistantMessage = {
  role: 'user' | 'assistant'
  content: string
  debug?: AssistantDebugEvent[]
}

type AssistantDebugEvent = {
  role: string
  type: string
  content: string
}

const TODAY_LABEL = new Intl.DateTimeFormat('en-US', {
  weekday: 'long',
  month: 'long',
  day: 'numeric',
}).format(new Date()).toUpperCase()

async function readAssistantDebugStream(
  response: Response,
  onEvent: (event: AssistantDebugEvent) => void,
) {
  if (!response.body) {
    throw new Error('The assistant debug stream was unavailable.')
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  const dispatchFrame = (frame: string) => {
    const data = frame
      .split(/\r?\n/)
      .filter((line) => line.startsWith('data:'))
      .map((line) => line.slice(5).trimStart())
      .join('\n')
    if (data) onEvent(JSON.parse(data) as AssistantDebugEvent)
  }

  try {
    while (true) {
      const { value, done } = await reader.read()
      buffer += decoder.decode(value, { stream: !done })
      let separator = buffer.search(/\r?\n\r?\n/)
      while (separator >= 0) {
        const frame = buffer.slice(0, separator)
        const separatorLength = buffer.slice(separator).match(/^\r?\n\r?\n/)?.[0].length ?? 2
        buffer = buffer.slice(separator + separatorLength)
        dispatchFrame(frame)
        separator = buffer.search(/\r?\n\r?\n/)
      }

      if (done) break
    }

    if (buffer.trim()) dispatchFrame(buffer)
  } finally {
    reader.releaseLock()
  }
}

function App() {
  const [authenticated, setAuthenticated] = useState(false)
  const [ready, setReady] = useState(false)
  const [page, setPage] = useState<'overview' | 'accounts' | 'transactions' | 'demo' | 'profile'>('overview')
  const [accountMenuOpen, setAccountMenuOpen] = useState(false)
  const [customer, setCustomer] = useState<Customer | null>(null)
  const [accounts, setAccounts] = useState<CustomerAccount[]>([])
  const [accountsLoading, setAccountsLoading] = useState(false)
  const [accountsRefreshVersion, setAccountsRefreshVersion] = useState(0)
  const [accountsError, setAccountsError] = useState<string | null>(null)
  const [creatingAccount, setCreatingAccount] = useState<AccountType | null>(null)
  const [overview, setOverview] = useState<OverviewSummary | null>(null)
  const [overviewLoading, setOverviewLoading] = useState(true)
  const [overviewRefreshVersion, setOverviewRefreshVersion] = useState(0)
  const [overviewError, setOverviewError] = useState<string | null>(null)
  const [transactions, setTransactions] = useState<TransactionPage>({
    items: [],
    page: 1,
    pageSize: 25,
    totalCount: 0,
  })
  const [transactionsLoading, setTransactionsLoading] = useState(false)
  const [transactionsRefreshVersion, setTransactionsRefreshVersion] = useState(0)
  const [transactionsError, setTransactionsError] = useState<string | null>(null)
  const [transactionAccountId, setTransactionAccountId] = useState('')
  const [transactionFromDate, setTransactionFromDate] = useState('')
  const [transactionToDate, setTransactionToDate] = useState('')
  const [transactionSearch, setTransactionSearch] = useState('')
  const [transactionPage, setTransactionPage] = useState(1)
  const [demoType, setDemoType] = useState<CustomerTransaction['type']>('EcommercePurchase')
  const [demoAccountId, setDemoAccountId] = useState('')
  const [demoDestinationAccountId, setDemoDestinationAccountId] = useState('')
  const [demoAmount, setDemoAmount] = useState('')
  const [demoLabel, setDemoLabel] = useState('')
  const [demoSubmitting, setDemoSubmitting] = useState(false)
  const [demoError, setDemoError] = useState<string | null>(null)
  const [demoSuccess, setDemoSuccess] = useState<string | null>(null)
  const [assistantOpen, setAssistantOpen] = useState(false)
  const [assistantMessages, setAssistantMessages] = useState<AssistantMessage[]>([])
  const [assistantInput, setAssistantInput] = useState('')
  const [assistantSending, setAssistantSending] = useState(false)
  const [assistantDebugMode, setAssistantDebugMode] = useState(false)
  const [assistantError, setAssistantError] = useState<string | null>(null)
  const [assistantNeedsLogin, setAssistantNeedsLogin] = useState(false)
  const [apiError, setApiError] = useState<string | null>(null)
  const [authError, setAuthError] = useState<string | null>(null)
  const accountMenuRef = useRef<HTMLDivElement>(null)
  const accountMenuButtonRef = useRef<HTMLButtonElement>(null)
  const assistantBodyRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (assistantSending && assistantBodyRef.current) {
      assistantBodyRef.current.scrollTop = assistantBodyRef.current.scrollHeight
    }
  }, [assistantMessages, assistantSending])

  useEffect(() => {
    initializeKeycloak()
      .then(setAuthenticated)
      .catch((error: unknown) => {
        setAuthError(error instanceof Error ? error.message : 'Could not connect to Keycloak.')
      })
      .finally(() => setReady(true))
  }, [])

  useEffect(() => {
    if (!authenticated) return

    const loadProfile = async () => {
      try {
        await keycloak.updateToken(30)
        const response = await fetch('/api/me', {
          headers: { Authorization: `Bearer ${keycloak.token}` },
        })

        if (!response.ok) {
          throw new Error(`Banking API returned ${response.status}.`)
        }

        setCustomer(await response.json() as Customer)
        setApiError(null)
      } catch (error) {
        setApiError(error instanceof Error ? error.message : 'Could not load your profile.')
      }
    }

    void loadProfile()
  }, [authenticated])

  useEffect(() => {
    if (!authenticated) return

    const controller = new AbortController()
    const loadOverview = async () => {
      setOverviewLoading(true)
      try {
        await keycloak.updateToken(30)
        const response = await fetch('/api/overview', {
          headers: { Authorization: `Bearer ${keycloak.token}` },
          signal: controller.signal,
        })
        if (!response.ok) {
          throw new Error(`Could not load your account overview (HTTP ${response.status}).`)
        }

        setOverview(await response.json() as OverviewSummary)
        setOverviewError(null)
      } catch (error) {
        if (!controller.signal.aborted) {
          setOverviewError(error instanceof Error
            ? error.message
            : 'Could not load your account overview.')
        }
      } finally {
        if (!controller.signal.aborted) setOverviewLoading(false)
      }
    }

    void loadOverview()
    return () => controller.abort()
  }, [authenticated, overviewRefreshVersion])

  useEffect(() => {
    if (!authenticated || page !== 'transactions') return

    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const loadTransactions = async () => {
        setTransactionsLoading(true)
        try {
          await keycloak.updateToken(30)
          const params = new URLSearchParams({
            page: String(transactionPage),
            pageSize: String(transactions.pageSize),
          })
          if (transactionAccountId) params.set('accountId', transactionAccountId)
          if (transactionFromDate) {
            params.set('from', getLocalDateBoundary(transactionFromDate).toISOString())
          }
          if (transactionToDate) {
            params.set('to', getLocalDateBoundary(transactionToDate, true).toISOString())
          }
          if (transactionSearch.trim()) params.set('search', transactionSearch.trim())

          const response = await fetch(`/api/transactions?${params}`, {
            headers: { Authorization: `Bearer ${keycloak.token}` },
            signal: controller.signal,
          })
          if (!response.ok) {
            throw new Error(`Could not load transactions (HTTP ${response.status}).`)
          }

          setTransactions(await response.json() as TransactionPage)
          setTransactionsError(null)
        } catch (error) {
          if (!controller.signal.aborted) {
            setTransactionsError(error instanceof Error
              ? error.message
              : 'Could not load transactions.')
          }
        } finally {
          if (!controller.signal.aborted) setTransactionsLoading(false)
        }
      }

      void loadTransactions()
    }, transactionSearch ? 250 : 0)

    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [
    authenticated,
    page,
    transactionAccountId,
    transactionFromDate,
    transactionToDate,
    transactionSearch,
    transactionPage,
    transactions.pageSize,
    transactionsRefreshVersion,
  ])

  useEffect(() => {
    if (!authenticated) return

    const loadAccounts = async () => {
      setAccountsLoading(true)
      try {
        await keycloak.updateToken(30)
        const response = await fetch('/api/accounts', {
          headers: { Authorization: `Bearer ${keycloak.token}` },
        })
        if (!response.ok) {
          throw new Error(`Could not load accounts (HTTP ${response.status}).`)
        }

        setAccounts(await response.json() as CustomerAccount[])
        setAccountsError(null)
      } catch (error) {
        setAccountsError(error instanceof Error ? error.message : 'Could not load accounts.')
      } finally {
        setAccountsLoading(false)
      }
    }

    void loadAccounts()
  }, [authenticated, accountsRefreshVersion])

  useEffect(() => {
    if (!accountMenuOpen) return

    const closeMenuOnOutsideClick = (event: PointerEvent) => {
      if (!accountMenuRef.current?.contains(event.target as Node)) {
        setAccountMenuOpen(false)
      }
    }
    const closeMenuOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setAccountMenuOpen(false)
        accountMenuButtonRef.current?.focus()
      }
    }

    document.addEventListener('pointerdown', closeMenuOnOutsideClick)
    document.addEventListener('keydown', closeMenuOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeMenuOnOutsideClick)
      document.removeEventListener('keydown', closeMenuOnEscape)
    }
  }, [accountMenuOpen])

  const signIn = () => {
    keycloak.login({ redirectUri: window.location.origin }).catch((error: unknown) => {
      setAuthError(error instanceof Error ? error.message : 'Could not start sign-in.')
    })
  }

  const register = () => {
    keycloak.register({ redirectUri: window.location.origin }).catch((error: unknown) => {
      setAuthError(error instanceof Error ? error.message : 'Could not start account creation.')
    })
  }

  const signOut = () => {
    setAssistantMessages([])
    setAssistantInput('')
    setAssistantError(null)
    setAssistantNeedsLogin(false)
    keycloak.logout({ redirectUri: window.location.origin }).catch((error: unknown) => {
      setAuthError(error instanceof Error ? error.message : 'Could not sign out.')
    })
  }

  const createAccount = async (type: AccountType) => {
    setCreatingAccount(type)
    setAccountsError(null)
    try {
      await keycloak.updateToken(30)
      const response = await fetch('/api/accounts', {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${keycloak.token}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ type }),
      })
      if (!response.ok) {
        throw new Error(`Could not open a ${type.toLowerCase()} account (HTTP ${response.status}).`)
      }

      const account = await response.json() as CustomerAccount
      setAccounts((current) => [account, ...current])
      setOverviewRefreshVersion((version) => version + 1)
    } catch (error) {
      setAccountsError(error instanceof Error ? error.message : 'Could not create the account.')
    } finally {
      setCreatingAccount(null)
    }
  }

  const createDemoTransaction = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setDemoSubmitting(true)
    setDemoError(null)
    setDemoSuccess(null)
    try {
      await keycloak.updateToken(30)
      const response = await fetch('/api/transactions/demo', {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${keycloak.token}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          accountId: demoAccountId,
          type: demoType,
          amount: Number(demoAmount),
          ...(demoType === 'MoneyTransfer' ? { destinationAccountId: demoDestinationAccountId } : {}),
          ...(demoLabel.trim() ? { label: demoLabel.trim() } : {}),
        }),
      })

      if (!response.ok) {
        const error = await response.json() as { detail?: unknown; title?: unknown }
        const message = typeof error.detail === 'string'
          ? error.detail
          : typeof error.title === 'string'
            ? error.title
            : `Could not create demo activity (HTTP ${response.status}).`
        throw new Error(message)
      }

      const result = await response.json() as { transactions: CustomerTransaction[] }
      const transfer = demoType === 'MoneyTransfer'
      setDemoSuccess(transfer
        ? 'Demo transfer recorded. Both accounts have been updated.'
        : 'Demo transaction recorded and account balance updated.')
      setDemoAmount('')
      setDemoLabel('')
      setAccountsRefreshVersion((version) => version + 1)
      setOverviewRefreshVersion((version) => version + 1)
      setTransactionsRefreshVersion((version) => version + 1)
      if (result.transactions.length > 0) {
        setTransactionAccountId('')
        setTransactionFromDate('')
        setTransactionToDate('')
        setTransactionSearch('')
        setTransactionPage(1)
      }
    } catch (error) {
      setDemoError(error instanceof Error ? error.message : 'Could not create demo activity.')
    } finally {
      setDemoSubmitting(false)
    }
  }

      const sendAssistantMessage = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault()
        const content = assistantInput.trim()
        if (!content || assistantSending) return

        if (!authenticated || !keycloak.authenticated) {
          setAssistantNeedsLogin(true)
          setAssistantError(null)
          return
        }

        setAssistantSending(true)
        setAssistantError(null)
        setAssistantNeedsLogin(false)
        const nextMessages = [...assistantMessages, { role: 'user' as const, content }]
        setAssistantMessages(nextMessages)
        setAssistantInput('')
        const contextMessages: AssistantMessage[] = []
        let contextLength = 0
        for (const message of nextMessages.slice(-8).reverse()) {
          const contextMessage = message.role === 'assistant' && message.content.length > 1_000
            ? {
                role: 'assistant' as const,
                content: 'A previous reply contained a long transaction list; retrieve relevant details again if needed.',
              }
            : { role: message.role, content: message.content }
          if (contextLength + contextMessage.content.length > 12_000) break
          contextMessages.push(contextMessage)
          contextLength += contextMessage.content.length
        }

        try {
          await keycloak.updateToken(30)
          if (!keycloak.authenticated || !keycloak.token) {
            setAssistantNeedsLogin(true)
            setAssistantError('Your session has expired. Sign in again to use the assistant.')
            return
          }

          const response = await fetch('/api/assistant/chat', {
            method: 'POST',
            headers: {
              Authorization: `Bearer ${keycloak.token}`,
              'Content-Type': 'application/json',
            },
            body: JSON.stringify({
              messages: contextMessages.reverse(),
              debugMode: assistantDebugMode,
            }),
          })
          if (response.status === 401) {
            setAssistantNeedsLogin(true)
            throw new Error('Your session has expired. Sign in again to use the assistant.')
          }
          if (!response.ok) {
            throw new Error(response.status === 503
              ? 'The assistant is temporarily unavailable. Please try again.'
              : `The assistant could not answer (HTTP ${response.status}).`)
          }

          if (assistantDebugMode) {
            setAssistantMessages((current) => [...current, {
              role: 'assistant',
              content: '',
              debug: [],
            }])
            let streamCompleted = false
            let streamFailed = false
            await readAssistantDebugStream(response, (debugEvent) => {
              if (debugEvent.type === 'complete') {
                streamCompleted = true
                return
              }

              if (debugEvent.type === 'error') {
                streamFailed = true
                setAssistantError(debugEvent.content)
              }

              setAssistantMessages((current) => current.map((message, index) => {
                if (index !== current.length - 1 || message.role !== 'assistant') return message
                const debug = message.debug ?? []
                const previousEvent = debug.at(-1)
                const isTextDelta = debugEvent.type === 'reasoning' ||
                  debugEvent.type === 'answer_delta' ||
                  debugEvent.type === 'model_output'
                const shouldAppendToPrevious = isTextDelta &&
                  previousEvent?.type === debugEvent.type &&
                  previousEvent.role === debugEvent.role
                const nextDebug = debugEvent.type === 'answer_delta'
                  ? debug
                  : shouldAppendToPrevious
                    ? [
                        ...debug.slice(0, -1),
                        { ...previousEvent, content: previousEvent.content + debugEvent.content },
                      ]
                    : [...debug, debugEvent]
                return {
                  ...message,
                  content: debugEvent.type === 'answer_delta'
                    ? message.content + debugEvent.content
                    : message.content,
                  debug: nextDebug,
                }
              }))
            })
            if (!streamCompleted && !streamFailed) {
              throw new Error('The assistant debug stream ended before completion.')
            }
          } else {
            const result = await response.json() as { reply: string }
            setAssistantMessages((current) => [...current, {
              role: 'assistant',
              content: result.reply,
            }])
          }
        } catch (error) {
          setAssistantMessages((current) => current.filter((message, index) =>
            index !== current.length - 1 || message.role !== 'assistant' || message.content.length > 0))
          setAssistantError(error instanceof Error ? error.message : 'Could not contact the assistant.')
        } finally {
          setAssistantSending(false)
        }
      }

      const assistantPanel = assistantOpen ? (
        <aside id="assistant-chat" className="assistant-chat-panel" aria-label="Ebank assistant">
          <div className="assistant-chat-header">
            <div>
              <span className="assistant-chat-symbol" aria-hidden="true">✳</span>
              <span>
                <strong>Ebank assistant</strong>
                <small>Read-only account help</small>
              </span>
            </div>
            <button type="button" className="assistant-close"
              aria-label="Close assistant" onClick={() => setAssistantOpen(false)}>×</button>
          </div>
          <div className="assistant-chat-body" aria-live="polite" ref={assistantBodyRef}>
            <div className="assistant-safety-note">
              Demo data only. The assistant can read your accounts and transactions; it cannot move money.
            </div>
            {(!authenticated || assistantNeedsLogin) ? (
              <div className="assistant-login-prompt">
                <h2>Sign in to get started</h2>
                <p>Sign in securely before asking about your accounts or transactions.</p>
                <button className="button button-primary" type="button" onClick={signIn}>
                  Sign in securely <span aria-hidden="true">→</span>
                </button>
              </div>
            ) : assistantMessages.length === 0 ? (
              <div className="assistant-welcome">
                <p>What would you like to know?</p>
                <ul>
                  <li>List my accounts</li>
                  <li>Show my five most recent transactions</li>
                  <li>Show transactions from 2026-09-01 to 2026-09-30</li>
                </ul>
              </div>
            ) : (
              <div className="assistant-chat-messages">
                {assistantMessages.map((message, index) => (
                  <div className={`assistant-chat-message ${message.role}`} key={`${message.role}-${index}`}>
                    <span>{message.role === 'user' ? 'You' : 'Ebank assistant'}</span>
                    <p>{message.content}</p>
                    {assistantDebugMode && message.role === 'assistant' && message.debug && (
                      <details className="assistant-debug-details" open>
                        <summary>Model debug ({message.debug.length} events)</summary>
                        {message.debug.length === 0 ? (
                          <p className="assistant-debug-empty">No model or tool events were returned.</p>
                        ) : message.debug.map((debugEvent, eventIndex) => (
                          <section className="assistant-debug-event" key={`${debugEvent.type}-${eventIndex}`}>
                            <span>{debugEvent.type} · {debugEvent.role}</span>
                            <pre>{debugEvent.content}</pre>
                          </section>
                        ))}
                      </details>
                    )}
                  </div>
                ))}
                {assistantSending && <p className="assistant-thinking">Checking your account data...</p>}
              </div>
            )}
            {assistantError && <p className="assistant-chat-error" role="alert">{assistantError}</p>}
          </div>
          {authenticated && !assistantNeedsLogin && (
            <form className="assistant-chat-form" onSubmit={(event) => void sendAssistantMessage(event)}>
              <label className="visually-hidden" htmlFor="assistant-message">Ask the Ebank assistant</label>
              <textarea id="assistant-message" value={assistantInput} maxLength={2_000}
                placeholder="Ask about your accounts..." rows={2}
                onChange={(event) => setAssistantInput(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' && !event.shiftKey) {
                    event.preventDefault()
                    event.currentTarget.form?.requestSubmit()
                  }
                }} />
              <label className="assistant-debug-toggle">
                <input
                  type="checkbox"
                  checked={assistantDebugMode}
                  disabled={assistantSending}
                  onChange={(event) => setAssistantDebugMode(event.target.checked)}
                />
                <span>Debug mode</span>
              </label>
              <button className="assistant-send" type="submit"
                disabled={assistantSending || !assistantInput.trim()}
                aria-label="Send message">
                {assistantSending ? '…' : '↑'}
              </button>
              <small>Your Keycloak session is verified before each request.</small>
            </form>
          )}
        </aside>
      ) : null

  if (!ready) {
    return (
      <main className="loading-screen">
        <span className="brand-mark">e</span>
        <p>Connecting securely...</p>
      </main>
    )
  }

  if (!authenticated) {
    return (
      <>
      <main className="welcome-screen">
        <section className="welcome-card">
          <a className="brand" href="/" aria-label="Ebank home">
            <span className="brand-mark">e</span>
            <span>ebank</span>
          </a>
          <div className="welcome-copy">
            <span className="eyebrow">A calmer kind of banking</span>
            <h1>Your money.<br />Moving with you.</h1>
            <p>A clear view of your finances, with a secure experience built around you.</p>
          </div>
          {authError && <p className="error-banner" role="alert">{authError}</p>}
          <button className="button button-primary sign-in" onClick={signIn}>
            Sign in securely <span aria-hidden="true">→</span>
          </button>
          <button className="register-button" onClick={register}>
            New to Ebank? <span>Create an account</span>
          </button>
          <p className="welcome-footnote">Protected by Ebank identity security</p>
        </section>
        <aside className="welcome-art" aria-label="Ebank secure banking">
          <div className="art-orbit orbit-one" />
          <div className="art-orbit orbit-two" />
          <div className="art-card">
            <div className="art-card-top"><span>ebank</span><span>●●●● 2048</span></div>
            <div className="art-card-chip"><span /><span /></div>
            <div className="art-card-bottom"><span>YOUR MONEY, IN GOOD HANDS</span><span>VISA</span></div>
          </div>
          <div className="art-caption">A little more clarity.<br />A lot more confidence.</div>
        </aside>
      </main>
      <button type="button" className="assistant-launcher" onClick={() => setAssistantOpen(true)}>
        <span aria-hidden="true">✳</span> Ask Ebank assistant
      </button>
      {assistantPanel}
      </>
    )
  }

  const fullName = [customer?.firstName, customer?.lastName]
    .filter((name): name is string => Boolean(name?.trim()))
    .join(' ')
  const greeting = fullName ? `Hello, ${fullName}` : 'Welcome to Ebank'
  const initials = [customer?.firstName, customer?.lastName]
    .filter((name): name is string => Boolean(name?.trim()))
    .map((name) => name.trim()[0].toUpperCase())
    .join('') || customer?.username?.slice(0, 1).toUpperCase() || 'E'

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <a className="brand" href="/" aria-label="Ebank home">
          <span className="brand-mark">e</span>
          <span>ebank</span>
        </a>
        <div className="workspace-tag"><span /> Demo workspace</div>
        <nav className="side-nav" aria-label="Main navigation">
          <button className={`nav-item ${page === 'overview' ? 'active' : ''}`}
            aria-current={page === 'overview' ? 'page' : undefined}
            onClick={() => setPage('overview')}>
            <span className="nav-icon">◫</span> Overview
          </button>
          <button className={`nav-item ${page === 'accounts' ? 'active' : ''}`}
            aria-current={page === 'accounts' ? 'page' : undefined}
            onClick={() => setPage('accounts')}>
            <span className="nav-icon">▤</span> Accounts
          </button>
          <button className={`nav-item ${page === 'transactions' ? 'active' : ''}`}
            aria-current={page === 'transactions' ? 'page' : undefined}
            onClick={() => setPage('transactions')}>
            <span className="nav-icon">↕</span> Transactions
          </button>
          <button className={`nav-item ${page === 'demo' ? 'active' : ''}`}
            aria-current={page === 'demo' ? 'page' : undefined}
            onClick={() => setPage('demo')}>
            <span className="nav-icon">✳</span> Demo
          </button>
        </nav>
      </aside>

      <section className="dashboard" id="overview">
        <header className="topbar">
          <div className="breadcrumb">My banking <span>/</span> {page === 'profile' ? 'Profile' : page === 'accounts' ? 'Accounts' : page === 'transactions' ? 'Transactions' : page === 'demo' ? 'Demo' : 'Overview'}</div>
          <div className="topbar-right">
            <button type="button" className="assistant-topbar-button"
              onClick={() => setAssistantOpen((open) => !open)}
              aria-expanded={assistantOpen} aria-controls="assistant-chat">
              <span aria-hidden="true">✳</span> Ask assistant
            </button>
            <div className="account-menu" ref={accountMenuRef}>
              <button
                className="profile-avatar-button"
                ref={accountMenuButtonRef}
                aria-label="Open account menu"
                aria-haspopup="menu"
                aria-expanded={accountMenuOpen}
                onClick={() => setAccountMenuOpen((open) => !open)}>
                <span className="avatar top-avatar">{initials}</span>
              </button>
              {accountMenuOpen && (
                <div className="account-menu-panel" role="menu" aria-label="Account">
                  <button role="menuitem" onClick={() => {
                    setPage('profile')
                    setAccountMenuOpen(false)
                  }}>
                    Profile
                  </button>
                  <button role="menuitem" onClick={() => {
                    setAccountMenuOpen(false)
                    signOut()
                  }}>
                    Log out
                  </button>
                </div>
              )}
            </div>
          </div>
        </header>

        <div className="dashboard-content">
          {page === 'overview' ? (
            <>
          <div className="greeting-row">
            <div>
              <span className="eyebrow">{TODAY_LABEL}</span>
              <h1>{greeting}<span className="greeting-period">.</span></h1>
              <p>Here’s what’s happening with your money.</p>
            </div>
          </div>

          {apiError && (
            <div className="error-banner" role="alert">
              You’re signed in, but the Ebank API is unavailable: {apiError}
            </div>
          )}
          {overviewError && <div className="error-banner" role="alert">{overviewError}</div>}

          <div className="overview-grid">
            <section className="balance-card">
              <div className="card-heading"><span>ACCOUNT BALANCE</span></div>
              {overviewLoading && !overview ? (
                <p className="overview-loading">Loading account balances...</p>
              ) : overview?.balances.length ? (
                <div className="overview-currency-totals">
                  {overview.balances.map((balance) => (
                    <div className="balance-amount" key={balance.currency}>
                      {formatCurrency(balance.amount, balance.currency)}
                    </div>
                  ))}
                </div>
              ) : (
                <div className="overview-no-accounts">
                  <div className="balance-amount">{formatCurrency(0, 'USD')}</div>
                  <p>No accounts yet. <button onClick={() => setPage('accounts')}>Open an account</button></p>
                </div>
              )}
              <div className="balance-foot">
                {overviewLoading && !overview ? 'Loading accounts...' : (
                  <>
                    <span>{overview?.accountCount ?? 0} accounts</span>
                    <span>·</span>
                    <span>{overview?.currentAccountCount ?? 0} current</span>
                    <span>·</span>
                    <span>{overview?.savingsAccountCount ?? 0} savings</span>
                  </>
                )}
              </div>
            </section>

            <section className="spending-card">
              <div className="card-heading"><span>SPENDING THIS MONTH (UTC)</span></div>
              {overviewLoading && !overview ? (
                <p className="overview-loading">Loading transactions...</p>
              ) : overview?.monthlySpending.length ? (
                overview.monthlySpending.map((total) => (
                  <div className="spending-amount" key={total.currency}>
                    {formatCurrency(total.amount, total.currency)}
                  </div>
                ))
              ) : (
                <p className="no-spending-data">No recorded spending this month.</p>
              )}
              <div className="overview-income">
                <span>Income this month</span>
                {overview?.monthlyIncome.length ? overview.monthlyIncome.map((total) => (
                  <strong key={total.currency}>{formatCurrency(total.amount, total.currency)}</strong>
                )) : <strong>No recorded income</strong>}
              </div>
            </section>
          </div>

          <div className="lower-grid">
            <section className="transactions-card" id="transactions">
              <div className="section-title-row">
                <div><span className="eyebrow">YOUR MONEY, IN MOTION</span><h2>Recent transactions</h2></div>
                <button onClick={() => setPage('transactions')}>View all <span>→</span></button>
              </div>
              {overviewLoading && !overview ? (
                <p className="overview-loading">Loading recent transactions...</p>
              ) : overview?.recentTransactions.length ? (
                <div className="transaction-list">
                  {overview.recentTransactions.map((transaction) => (
                  <div className="transaction-row" key={transaction.id}>
                    <span className="transaction-description">
                        <strong>{transaction.label}</strong>
                        <small>{transaction.accountType} account · {formatDateTime(transaction.occurredAt)}</small>
                      </span>
                      <strong className={`transaction-amount ${transaction.amount > 0 ? 'positive' : ''}`}>
                        {transaction.amount > 0 ? '+' : ''}
                        {formatCurrency(transaction.amount, transaction.currency)}
                      </strong>
                    </div>
                  ))}
                </div>
              ) : (
                <p className="overview-empty-transactions">No transactions have been recorded yet.</p>
              )}
            </section>

            <section className="assistant-card">
              <div className="assistant-top"><span className="assistant-symbol">✳</span><span className="preview-tag">COMING NEXT</span></div>
              <span className="eyebrow">YOUR PERSONAL MONEY GUIDE</span>
              <h2>A little help,<br />right when you need it.</h2>
              <p>Your Ebank assistant is being built to help you understand spending and find answers, while keeping you in control.</p>
              <div className="assistant-prompt"><span>✧</span><span>Read-only answers. You approve every action.</span></div>
              <div className="assistant-disclaimer">AI assistant is not connected yet. No financial actions are available.</div>
            </section>
          </div>
          <footer className="demo-footer"><span>Balances and activity are loaded from your Ebank database.</span></footer>
            </>
          ) : page === 'accounts' ? (
            <>
              <div className="accounts-page-heading">
                <div>
                  <span className="eyebrow">YOUR BANKING</span>
                  <h1>Accounts</h1>
                  <p>View your accounts or open a new current or savings account.</p>
                </div>
              </div>

              {accountsError && <div className="error-banner" role="alert">{accountsError}</div>}

              <section className="open-account-panel" aria-labelledby="open-account-title">
                <div>
                  <span className="eyebrow">GET STARTED</span>
                  <h2 id="open-account-title">Open an account</h2>
                  <p>Choose the type of demo account you would like to create.</p>
                </div>
                <div className="open-account-actions">
                  <button className="account-open-button" disabled={accountsLoading || creatingAccount !== null}
                    onClick={() => void createAccount('Current')}>
                    {creatingAccount === 'Current' ? 'Opening...' : 'Open current account'}
                  </button>
                  <button className="account-open-button secondary" disabled={accountsLoading || creatingAccount !== null}
                    onClick={() => void createAccount('Savings')}>
                    {creatingAccount === 'Savings' ? 'Opening...' : 'Open savings account'}
                  </button>
                </div>
              </section>

              <section className="customer-accounts" aria-labelledby="customer-accounts-title">
                <div className="section-title-row">
                  <div>
                    <span className="eyebrow">YOUR MONEY, ORGANIZED</span>
                    <h2 id="customer-accounts-title">Your accounts</h2>
                  </div>
                  <span className="account-count">{accounts.length} {accounts.length === 1 ? 'account' : 'accounts'}</span>
                </div>

                {accountsLoading ? (
                  <p className="accounts-empty">Loading your accounts...</p>
                ) : accounts.length === 0 ? (
                  <p className="accounts-empty">You don’t have any accounts yet. Open a current or savings account above.</p>
                ) : (
                  <div className="accounts-table-wrapper">
                    <table className="accounts-table">
                      <thead>
                        <tr>
                          <th scope="col">Account type</th>
                          <th scope="col">Account number</th>
                          <th scope="col">Available balance</th>
                          <th scope="col">Created at</th>
                        </tr>
                      </thead>
                      <tbody>
                        {accounts.map((account) => (
                          <tr key={account.id}>
                            <td className="accounts-table-type">
                              <span className="account-type-icon" aria-hidden="true">
                                {account.type === 'Current' ? '▤' : '◷'}
                              </span>
                              <span>{account.type} account</span>
                            </td>
                            <td className="accounts-table-number">{account.accountNumber}</td>
                            <td className="accounts-table-balance">{new Intl.NumberFormat('en-US', {
                              style: 'currency',
                              currency: account.currency,
                            }).format(account.balance)}</td>
                            <td>{new Intl.DateTimeFormat('en-US', {
                              year: 'numeric',
                              month: 'short',
                              day: 'numeric',
                              hour: 'numeric',
                              minute: '2-digit',
                            }).format(new Date(account.createdAt))}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                <p className="account-demo-note">
                  Demo accounts are illustrative records only; they are not real bank accounts or payment services.
                </p>
              </section>
            </>
          ) : page === 'transactions' ? (
            <>
              <div className="transactions-page-heading">
                <div>
                  <span className="eyebrow">YOUR MONEY, IN MOTION</span>
                  <h1>Transactions</h1>
                  <p>Review activity across all your accounts, newest first.</p>
                </div>
              </div>

              <section className="transaction-filters" aria-label="Filter transactions">
                <label>
                  <span>Account</span>
                  <select value={transactionAccountId} onChange={(event) => {
                    setTransactionAccountId(event.target.value)
                    setTransactionPage(1)
                  }}>
                    <option value="">All accounts</option>
                    {accounts.map((account) => (
                      <option value={account.id} key={account.id}>
                        {account.type} account · {account.accountNumber}
                      </option>
                    ))}
                  </select>
                </label>
                <label>
                  <span>From</span>
                  <input type="date" value={transactionFromDate} onChange={(event) => {
                    setTransactionFromDate(event.target.value)
                    setTransactionPage(1)
                  }} />
                </label>
                <label>
                  <span>To</span>
                  <input type="date" value={transactionToDate} onChange={(event) => {
                    setTransactionToDate(event.target.value)
                    setTransactionPage(1)
                  }} />
                </label>
                <label className="transaction-search">
                  <span>Search name or label</span>
                  <input type="search" maxLength={100} placeholder="e.g. groceries"
                    value={transactionSearch} onChange={(event) => {
                      setTransactionSearch(event.target.value)
                      setTransactionPage(1)
                    }} />
                </label>
                <button className="clear-transaction-filters" onClick={() => {
                  setTransactionAccountId('')
                  setTransactionFromDate('')
                  setTransactionToDate('')
                  setTransactionSearch('')
                  setTransactionPage(1)
                }}>Clear filters</button>
              </section>

              {transactionsError && <div className="error-banner" role="alert">{transactionsError}</div>}

              <section className="transaction-history" aria-label="Transaction history">
                {transactionsLoading ? (
                  <p className="transactions-empty">Loading transactions...</p>
                ) : transactions.items.length === 0 ? (
                  <p className="transactions-empty">
                    No transactions found. Account activity will appear here once transactions are recorded.
                  </p>
                ) : (
                  <div className="transactions-table-wrapper">
                    <table className="transactions-table">
                      <thead>
                        <tr>
                          <th scope="col">Date and time</th>
                          <th scope="col">Name / label</th>
                          <th scope="col">Account</th>
                          <th scope="col">Amount</th>
                        </tr>
                      </thead>
                      <tbody>
                        {transactions.items.map((transaction) => (
                          <tr key={transaction.id}>
                            <td className="transaction-date">{new Intl.DateTimeFormat('en-US', {
                              year: 'numeric',
                              month: 'short',
                              day: 'numeric',
                              hour: 'numeric',
                              minute: '2-digit',
                            }).format(new Date(transaction.occurredAt))}</td>
                            <td className="transaction-label">{transaction.label}</td>
                            <td className="transaction-account">
                              <span>{transaction.accountType} account</span>
                              <small>{transaction.accountNumber}</small>
                            </td>
                            <td className={`transaction-history-amount ${transaction.amount > 0 ? 'credit' : ''}`}>
                              {transaction.amount > 0 ? '+' : ''}
                              {new Intl.NumberFormat('en-US', {
                                style: 'currency',
                                currency: transaction.currency,
                              }).format(transaction.amount)}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                {transactions.totalCount > transactions.pageSize && (
                  <div className="transaction-pagination">
                    <span>
                      Page {transactions.page} of {Math.ceil(transactions.totalCount / transactions.pageSize)}
                      {' · '}{transactions.totalCount} transactions
                    </span>
                    <div>
                      <button disabled={transactionsLoading || transactions.page <= 1}
                        onClick={() => setTransactionPage((current) => Math.max(1, current - 1))}>
                        Previous
                      </button>
                      <button disabled={transactionsLoading ||
                        transactions.page >= Math.ceil(transactions.totalCount / transactions.pageSize)}
                        onClick={() => setTransactionPage((current) => current + 1)}>
                        Next
                      </button>
                    </div>
                  </div>
                )}
              </section>
              <p className="transactions-timezone-note">Date filters use your local timezone.</p>
            </>
          ) : page === 'demo' ? (
            <>
              <div className="transactions-page-heading">
                <div>
                  <span className="eyebrow">ILLUSTRATIVE ACTIVITY</span>
                  <h1>Demo transactions</h1>
                  <p>Create sample activity to explore account balances and transaction history.</p>
                </div>
              </div>

              <div className="demo-warning" role="note">
                Demo only. This records illustrative activity in Ebank and does not move real money.
              </div>
              {demoError && <div className="error-banner" role="alert">{demoError}</div>}
              {demoSuccess && <div className="success-banner" role="status">{demoSuccess}</div>}

              <form className="demo-transaction-form" onSubmit={(event) => void createDemoTransaction(event)}>
                <label>
                  <span>Transaction type</span>
                  <select value={demoType} onChange={(event) => {
                    setDemoType(event.target.value as CustomerTransaction['type'])
                    setDemoError(null)
                    setDemoSuccess(null)
                  }}>
                    <option value="EcommercePurchase">E-commerce spending</option>
                    <option value="AtmWithdrawal">ATM withdrawal</option>
                    <option value="SalaryReceived">Salary received</option>
                    <option value="MoneyTransfer">Money transfer</option>
                  </select>
                </label>
                <label>
                  <span>{demoType === 'SalaryReceived' ? 'Credit account' : 'Source account'}</span>
                  <select required value={demoAccountId} onChange={(event) => {
                    setDemoAccountId(event.target.value)
                    if (event.target.value === demoDestinationAccountId) setDemoDestinationAccountId('')
                    setDemoError(null)
                    setDemoSuccess(null)
                  }}>
                    <option value="">Select an account</option>
                    {accounts.map((account) => (
                      <option value={account.id} key={account.id}>
                        {account.type} · {account.accountNumber} · {formatCurrency(account.balance, account.currency)}
                      </option>
                    ))}
                  </select>
                </label>
                {demoType === 'MoneyTransfer' && (
                  <label>
                    <span>Destination account</span>
                    <select required value={demoDestinationAccountId} onChange={(event) => {
                      setDemoDestinationAccountId(event.target.value)
                      setDemoError(null)
                      setDemoSuccess(null)
                    }}>
                      <option value="">Select a destination</option>
                      {accounts.filter((account) => account.id !== demoAccountId).map((account) => (
                        <option value={account.id} key={account.id}>
                          {account.type} · {account.accountNumber} · {formatCurrency(account.balance, account.currency)}
                        </option>
                      ))}
                    </select>
                  </label>
                )}
                <label>
                  <span>Amount</span>
                  <input required type="number" min="0.01" max="1000000" step="0.01"
                    value={demoAmount} onChange={(event) => {
                      setDemoAmount(event.target.value)
                      setDemoError(null)
                      setDemoSuccess(null)
                    }} placeholder="0.00" />
                </label>
                <label className="demo-label-field">
                  <span>Label <small>(optional)</small></span>
                  <input type="text" maxLength={160} value={demoLabel}
                    onChange={(event) => {
                      setDemoLabel(event.target.value)
                      setDemoError(null)
                      setDemoSuccess(null)
                    }} placeholder={demoType === 'EcommercePurchase' ? 'e.g. Online shopping' : 'Use a suggested label'} />
                </label>
                {accounts.length === 0 && !accountsLoading && (
                  <p className="demo-no-accounts">Open a demo account before creating activity. <button type="button" onClick={() => setPage('accounts')}>Go to Accounts</button></p>
                )}
                <div className="demo-form-actions">
                  <button className="button button-primary" type="submit"
                    disabled={demoSubmitting || accountsLoading || accounts.length === 0}>
                    {demoSubmitting ? 'Recording...' : 'Create demo transaction'}
                  </button>
                </div>
              </form>
              <p className="transactions-timezone-note">
                Spending, withdrawals and transfers require sufficient balance. Transfers create matching entries on both accounts.
              </p>
            </>
          ) : (
            <>
              <div className="profile-page-heading">
                <div>
                  <span className="eyebrow">YOUR ACCOUNT</span>
                  <h1>Profile</h1>
                  <p>Manage your personal details and customer verification.</p>
                </div>
              </div>
              <KycSection />
            </>
          )}
        </div>
      </section>
      <button type="button" className="assistant-launcher dashboard-assistant-launcher"
        onClick={() => setAssistantOpen(true)}>
        <span aria-hidden="true">✳</span> Ask Ebank assistant
      </button>
      {assistantPanel}
    </main>
  )
}

function getLocalDateBoundary(date: string, nextDay = false) {
  const [year, month, day] = date.split('-').map(Number)
  const boundary = new Date(year, month - 1, day)
  if (nextDay) boundary.setDate(boundary.getDate() + 1)
  return boundary
}

function formatCurrency(amount: number, currency: string) {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency,
  }).format(amount)
}

function formatDateTime(date: string) {
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(new Date(date))
}

export default App
