import { useEffect, useRef, useState, type FormEvent } from 'react'
import { createRoot } from 'react-dom/client'
import {
  ArrowRight, ArrowUp, BookOpen, ChevronRight, FlaskConical, Languages, LogOut,
  Menu, MessageCircle, Sparkles, X,
} from 'lucide-react'
import './style.css'

type Mode = 'learning' | 'practice'
type Session = { signedIn: boolean; name: string | null; demo: boolean; logoutToken: string | null }
type LessonLink = { id: string; title: string; targetLanguage: string }
type Lesson = LessonLink & { openingContent: string; examples: { phrase: string; translation: string }[] }
type Turn = { requestId: string; revision: number; message: string; reply: string }
type Chat = { lesson: Lesson; revision: number; turns: Turn[] }
type MessageRequest = { requestId: string; expectedRevision: number; message: string }

const modes = {
  learning: { label: 'Learning', icon: BookOpen, prompt: 'Ask about this lesson…',
    starters: ['Explain these phrases', 'Give me another example'] },
  practice: { label: 'Practice', icon: MessageCircle, prompt: 'Type your answer or ask to practice…',
    starters: ['Quiz me on this lesson', 'Let’s practice a conversation'] },
}

class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
  }
}

async function api<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(path, options)
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    const detail = response.status === 401
      ? 'Your session expired. Sign in again to continue.'
      : problem?.detail ?? 'Something went wrong. Please try again.'
    throw new ApiError(response.status, detail)
  }
  return response.json()
}

function initialSelection() {
  const query = new URLSearchParams(location.search)
  return {
    mode: (query.get('mode') === 'practice' ? 'practice' : 'learning') as Mode,
    lessonId: query.get('lesson') ?? '',
  }
}

function Brand() {
  return <div className="brand">
    <span className="brand-mark"><Languages size={21} strokeWidth={2.2} /></span>
    <span>murly<span className="brand-ai"> ai</span></span>
  </div>
}

function App() {
  const [session, setSession] = useState<Session>()
  const [lessons, setLessons] = useState<LessonLink[]>()
  const [error, setError] = useState('')
  const [attempt, setAttempt] = useState(0)
  const [selection, setSelection] = useState(initialSelection)
  const [drawer, setDrawer] = useState(false)
  const { mode, lessonId } = selection
  const selected = lessons?.find(lesson => lesson.id === lessonId) ?? lessons?.[0]

  useEffect(() => {
    const controller = new AbortController()
    const options = { signal: controller.signal }
    async function load() {
      setError('')
      try {
        const current = await api<Session>('/api/session', options)
        setSession(current)
        if (current.signedIn) setLessons(await api<LessonLink[]>('/api/lessons', options))
      } catch (failure) {
        if (!controller.signal.aborted) setError((failure as Error).message)
      }
    }
    void load()
    return () => controller.abort()
  }, [attempt])

  useEffect(() => {
    if (!selected) return
    const query = new URLSearchParams({ mode, lesson: selected.id })
    history.replaceState(null, '', '?' + query.toString())
  }, [mode, selected])

  useEffect(() => {
    if (!drawer) return
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setDrawer(false)
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [drawer])

  if (session && !session.signedIn) {
    return <main className="welcome">
      <div className="welcome-card">
        <Brand />
        <h1>Find your words.</h1>
        <p>Learn a language one conversation at a time.</p>
        <a className="primary" href="/auth/login">Sign in to Murly <ArrowRight size={17} /></a>
      </div>
    </main>
  }

  return <div className="app-shell">
    {drawer && <button className="scrim" aria-label="Close sidebar" onClick={() => setDrawer(false)} />}
    <aside className={'sidebar' + (drawer ? ' is-open' : '')} id="lesson-sidebar">
      <div className="sidebar-top">
        <Brand />
        <button className="icon-button close-drawer" aria-label="Close sidebar" onClick={() => setDrawer(false)}>
          <X size={21} />
        </button>
      </div>
      <nav aria-label="Study mode" className="mode-switch">
        {(Object.keys(modes) as Mode[]).map(key => {
          const Icon = modes[key].icon
          return <button key={key} aria-pressed={mode === key}
            onClick={() => { setSelection({ mode: key, lessonId }); setDrawer(false) }}>
            <Icon size={16} /> {modes[key].label}
          </button>
        })}
      </nav>
      <nav className="lesson-nav" aria-label="Lessons">
        <h2>Lessons</h2>
        {lessons?.map(lesson => <button key={lesson.id}
          aria-current={selected?.id === lesson.id ? 'true' : undefined}
          onClick={() => { setSelection({ mode, lessonId: lesson.id }); setDrawer(false) }}>
          <BookOpen size={17} />
          <span>{lesson.title}</span>
          {selected?.id === lesson.id && <ChevronRight className="aside-chevron" size={15} />}
        </button>)}
      </nav>
      {session && <div className="account">
        <span className="avatar" aria-hidden="true">{(session.name ?? 'L').trim().slice(0, 1).toUpperCase()}</span>
        <div className="account-name">
          <strong>{session.name}</strong>
          {session.demo && <span>Local demo</span>}
        </div>
        {session.logoutToken && <form method="post" action="/auth/logout">
          <input type="hidden" name="__RequestVerificationToken" value={session.logoutToken} />
          <button className="icon-button" aria-label="Sign out" title="Sign out"><LogOut size={17} /></button>
        </form>}
      </div>}
    </aside>

    <main className="workspace">
      <header className="topbar">
        <button className="icon-button open-drawer" aria-label="Open sidebar" aria-controls="lesson-sidebar"
          aria-expanded={drawer} onClick={() => setDrawer(true)}><Menu size={22} /></button>
        <div className="breadcrumbs">
          <span>{modes[mode].label}</span>
          {selected && <><ChevronRight size={14} /><strong>{selected.title}</strong></>}
        </div>
        {selected && <span className="language"><Languages size={15} /> {selected.targetLanguage}</span>}
      </header>
      {error
        ? <div className="status" role="alert"><p>{error}</p>
          <button className="secondary" onClick={() => setAttempt(value => value + 1)}>Try again</button></div>
        : selected
          ? <Conversation key={mode + ':' + selected.id} mode={mode} lessonId={selected.id} demo={session?.demo ?? false} />
          : <div className="status" role="status">{lessons ? 'No lessons are available yet.' : 'Loading lessons…'}</div>}
    </main>
  </div>
}

function RichText({ text }: { text: string }) {
  return <>{text.split(/(\*\*[^*]+\*\*)/g).map((part, index) =>
    part.startsWith('**') && part.endsWith('**')
      ? <strong key={index}>{part.slice(2, -2)}</strong>
      : part)}</>
}

function Conversation({ mode, lessonId, demo }: { mode: Mode; lessonId: string; demo: boolean }) {
  const [chat, setChat] = useState<Chat>()
  const [draft, setDraft] = useState('')
  const [error, setError] = useState('')
  const [authExpired, setAuthExpired] = useState(false)
  const [sending, setSending] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [refreshFailed, setRefreshFailed] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const retry = useRef<MessageRequest | null>(null)
  const area = useRef<HTMLTextAreaElement>(null)
  const latest = useRef<HTMLDivElement>(null)
  const config = modes[mode]
  const path = '/api/lessons/' + encodeURIComponent(lessonId) + '/chats/' + mode

  useEffect(() => {
    const controller = new AbortController()
    api<Chat>(path, { signal: controller.signal })
      .then(current => {
        setChat(current)
        setRefreshing(false)
      })
      .catch(failure => {
        if (!controller.signal.aborted) {
          setError(failure.message)
          setAuthExpired(failure.status === 401)
          setRefreshFailed(true)
        }
      })
    return () => controller.abort()
  }, [path, attempt])

  async function send(event?: FormEvent) {
    event?.preventDefault()
    const message = draft.trim()
    if (!chat || !message || message.length > 4000 || sending || refreshing || authExpired) return

    const request = retry.current?.message === message && retry.current.expectedRevision === chat.revision
      ? retry.current : { requestId: crypto.randomUUID(), expectedRevision: chat.revision, message }
    retry.current = request
    setError('')
    setSending(true)
    try {
      const turn = await api<Turn>(path + '/messages', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      })
      retry.current = null
      setChat({ ...chat, revision: turn.revision, turns: [...chat.turns, turn] })
      setDraft('')
      requestAnimationFrame(() => latest.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }))
    } catch (failure) {
      setError((failure as Error).message)
      if (failure instanceof ApiError && failure.status === 401) setAuthExpired(true)
      if (failure instanceof ApiError && failure.status === 409) {
        retry.current = null
        setRefreshing(true)
        setAttempt(value => value + 1)
      }
    } finally {
      setSending(false)
    }
  }

  if (!chat) return <div className="status" role={error ? 'alert' : 'status'}>
    {error || 'Opening lesson…'}
    {authExpired ? <a className="secondary" href="/auth/login">Sign in</a> : error &&
      <button className="secondary" onClick={() => { setError(''); setAttempt(value => value + 1) }}>Try again</button>}
  </div>

  return <>
    <div className="conversation-scroll">
      <div className="conversation">
        <section className="lesson" aria-labelledby="lesson-title">
          <span className="lesson-icon"><BookOpen size={24} strokeWidth={1.7} /></span>
          <h1 id="lesson-title">{chat.lesson.title}</h1>
          <div className="lesson-opening"><RichText text={chat.lesson.openingContent} /></div>
          {chat.lesson.examples.length > 0 && <dl className="examples">
            {chat.lesson.examples.map((example, index) => <div key={index}>
              <dt>{example.phrase}</dt><dd>{example.translation}</dd>
            </div>)}
          </dl>}
        </section>

        {chat.turns.length === 0 && <section className="starter" aria-label="Conversation starters">
          <div className="starter-label"><Sparkles size={16} /><span>{mode === 'learning' ? 'Continue with Murly' : 'Practice with Murly'}</span></div>
          <div className="starter-actions">{config.starters.map(prompt =>
            <button key={prompt} disabled={sending} onClick={() => { setDraft(prompt); area.current?.focus() }}>
              {prompt} <ArrowRight size={14} />
            </button>
          )}</div>
        </section>}
        <div className="turns" aria-live="polite">
          {chat.turns.map(turn => <div className="turn" key={turn.requestId}>
            <div className="learner-message"><span className="sr-only">You: </span>{turn.message}</div>
            <div className="tutor-message">
              <span className="tutor-icon"><Sparkles size={15} /></span>
              <div><b>Murly</b><p><RichText text={turn.reply} /></p></div>
            </div>
          </div>)}
          {sending && <div className="thinking"><Sparkles size={16} /> Murly is replying…</div>}
          <div ref={latest} />
        </div>
      </div>
    </div>
    <footer className="composer">
      <div className="composer-inner">
        {error && <div className="send-error" role="alert">{error}{' '}
          {authExpired ? <a href="/auth/login">Sign in</a> : refreshing && refreshFailed &&
            <button className="secondary" onClick={() => { setRefreshFailed(false); setAttempt(value => value + 1) }}>
              Reload chat
            </button>}
        </div>}
        <form onSubmit={send} className="prompt">
          <textarea ref={area} aria-label="Message Murly" placeholder={config.prompt} value={draft}
            maxLength={4000} rows={1} readOnly={sending} onChange={event => setDraft(event.target.value)}
            onKeyDown={event => {
              if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
                event.preventDefault()
                void send()
              }
            }} />
          <button className="send" aria-label="Send message" title="Send message"
            disabled={sending || refreshing || authExpired || !draft.trim()}><ArrowUp size={21} strokeWidth={2.1} /></button>
        </form>
        {demo && <div className="demo-note"><FlaskConical size={13} /> Demo lessons</div>}
      </div>
    </footer>
  </>
}

createRoot(document.getElementById('root')!).render(<App />)
