import type {ReactNode} from 'react';
import {useEffect, useMemo, useState} from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';

import styles from './theme-gallery.module.css';

type Theme = {
  name: string;
  slug: string;
  dark: boolean;
  background: string;
  foreground: string;
  cursorColor?: string;
  selectionBackground?: string;
  palette: string[];
};

type Catalog = {
  count: number;
  source: string;
  themes: Theme[];
};

type Filter = 'all' | 'dark' | 'light';

const PAGE = 60;

function Preview({theme}: {theme: Theme}) {
  const p = (i: number) => ({color: theme.palette[i] ?? theme.foreground});
  return (
    <pre className={styles.preview} style={{background: theme.background, color: theme.foreground}}>
      <span style={{...p(2), fontWeight: 700}}>ryan@host</span>:<span style={{...p(4), fontWeight: 700}}>~/code</span>
      <span style={p(8)}> (</span>
      <span style={p(5)}>main</span>
      <span style={p(8)}>)</span> $ ls{'\n'}
      <span style={{...p(4), fontWeight: 700}}>docs/</span>  <span style={{...p(2), fontWeight: 700}}>run.sh</span>  README.md{'\n'}
      <span style={p(1)}> M</span> MainWindow.cs  <span style={p(3)}>warning</span>{'\n'}
      $ <span className={styles.cursor} style={{background: theme.cursorColor ?? theme.foreground}}> </span>
    </pre>
  );
}

function Swatches({theme}: {theme: Theme}) {
  return (
    <div className={styles.swatches}>
      {theme.palette.map((c, i) => (
        <span key={i} className={styles.swatch} style={{background: c}} title={`${i}: ${c}`} />
      ))}
    </div>
  );
}

function ThemeCard({theme, base}: {theme: Theme; base: string}): ReactNode {
  const [copied, setCopied] = useState(false);
  const command = `wraithcairn --set-theme "${theme.name}"`;
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard blocked; the command is in the title attribute
    }
  };

  return (
    <div className={styles.card}>
      <Preview theme={theme} />
      <div className={styles.cardBody}>
        <div className={styles.cardTitle}>
          <strong>{theme.name}</strong>
          <span className={styles.badge}>{theme.dark ? 'dark' : 'light'}</span>
        </div>
        <Swatches theme={theme} />
        <div className={styles.cardActions}>
          <a href={`${base}ghostty/${theme.slug}`} download={theme.name}>
            Download
          </a>
          <button type="button" className={styles.linkButton} onClick={copy} title={command}>
            {copied ? 'Copied' : 'Copy command'}
          </button>
        </div>
      </div>
    </div>
  );
}

export default function Themes(): ReactNode {
  const base = useBaseUrl('/themes/');
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const [shown, setShown] = useState(PAGE);

  useEffect(() => {
    fetch(`${base}index.json`)
      .then((r) => {
        if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
        return r.json();
      })
      .then((c: Catalog) => setCatalog(c))
      .catch((e: Error) => setError(e.message));
  }, [base]);

  const matches = useMemo(() => {
    if (!catalog) return [];
    const q = query.trim().toLowerCase();
    return catalog.themes.filter(
      (t) =>
        (filter === 'all' || (filter === 'dark') === t.dark) &&
        (q.length === 0 || t.name.toLowerCase().includes(q)),
    );
  }, [catalog, query, filter]);

  useEffect(() => setShown(PAGE), [query, filter]);

  return (
    <Layout title="Themes" description="Every Ghostty color theme, ready for Wraithcairn">
      <main className="container margin-vert--lg">
        <Heading as="h1">Themes</Heading>
        <p>
          The {catalog ? catalog.count : '800+'} color themes built into{' '}
          <a href="https://ghostty.org">Ghostty</a>, ready for Wraithcairn. In the app, open{' '}
          <strong>View → Themes…</strong> to preview and apply one live (no restart), or grab them all at
          once. See the <Link to="/docs/guides/themes">Themes guide</Link>.
        </p>

        <div className={styles.toolbar}>
          <input
            className={styles.search}
            type="search"
            placeholder="Search themes…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            aria-label="Search themes"
          />
          <div className={styles.filters} role="tablist" aria-label="Filter">
            {(['all', 'dark', 'light'] as Filter[]).map((f) => (
              <button
                key={f}
                type="button"
                role="tab"
                aria-selected={filter === f}
                className={clsx('button button--sm', filter === f ? 'button--primary' : 'button--secondary')}
                onClick={() => setFilter(f)}>
                {f[0].toUpperCase() + f.slice(1)}
              </button>
            ))}
          </div>
          <a className="button button--primary button--sm" href={`${base}wraithcairn-themes.zip`} download>
            Download all (.zip)
          </a>
        </div>

        {error && <p className={styles.error}>Could not load the theme list: {error}</p>}
        {catalog && (
          <p className={styles.count}>
            {matches.length} of {catalog.count} themes
          </p>
        )}

        <div className={styles.grid}>
          {matches.slice(0, shown).map((t) => (
            <ThemeCard key={t.slug} theme={t} base={base} />
          ))}
        </div>

        {shown < matches.length && (
          <div className={styles.more}>
            <button type="button" className="button button--secondary" onClick={() => setShown(shown + PAGE * 2)}>
              Show more ({matches.length - shown} left)
            </button>
          </div>
        )}

        <p className={styles.notice}>
          Themes from <a href="https://github.com/mbadolato/iTerm2-Color-Schemes">iTerm2-Color-Schemes</a> (MIT)
          as bundled with {catalog?.source.replace(/ built-in themes.*/, '') ?? 'Ghostty'}. Each theme's colors
          belong to its original author.
        </p>
      </main>
    </Layout>
  );
}
