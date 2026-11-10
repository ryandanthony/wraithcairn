import type {ReactNode} from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import useBaseUrl from '@docusaurus/useBaseUrl';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';

import styles from './index.module.css';

type FeatureItem = {
  title: string;
  description: ReactNode;
  link: string;
};

const FeatureList: FeatureItem[] = [
  {
    title: 'Layouts, Not Tabs',
    description: (
      <>
        A window holds named <strong>layouts</strong> — each a set of split
        panes — and shows one at a time. Switch instantly with{' '}
        <code>Alt+1..9</code>; layouts you switch away from keep running in
        the background.
      </>
    ),
    link: '/docs/guides/layouts',
  },
  {
    title: 'Per-Pane Startup',
    description: (
      <>
        Give any pane its own starting folder, program, and arguments. Run
        it inside the shell or exec it directly, and restart it on demand
        with <code>Ctrl+Shift+R</code>.
      </>
    ),
    link: '/docs/guides/per-pane-startup',
  },
  {
    title: 'Everything Restores',
    description: (
      <>
        Windows, layouts, splits, ratios, and per-pane working directories
        are saved automatically and restored the next time you launch.
      </>
    ),
    link: '/docs/guides/configuration',
  },
  {
    title: 'Built on Ghostty',
    description: (
      <>
        Powered by the <code>libghostty-vt</code> engine via the
        RoyalTerminal packages, with Ghostty config and theme compatibility
        out of the box.
      </>
    ),
    link: '/docs/guides/configuration',
  },
];

function Feature({title, description, link}: FeatureItem) {
  return (
    <div className={clsx('col col--6', styles.feature)}>
      <div className="padding-horiz--md padding-vert--md">
        <Heading as="h3">
          <Link to={link}>{title}</Link>
        </Heading>
        <p>{description}</p>
      </div>
    </div>
  );
}

function HomepageHeader() {
  const {siteConfig} = useDocusaurusContext();
  const logoSrc = useBaseUrl('img/icon.svg');
  return (
    <header className={clsx('hero hero--primary', styles.heroBanner)}>
      <div className="container">
        <img src={logoSrc} alt="Wraithcairn" className={styles.logo} />
        <div className={styles.cliBadge}>
          <span className={styles.prompt}>&gt;</span> wraithcairn --workspace main
        </div>
        <Heading as="h1" className="hero__title">
          {siteConfig.title}
        </Heading>
        <p className="hero__subtitle">{siteConfig.tagline}</p>
        <div className={styles.buttons}>
          <Link
            className="button button--secondary button--lg"
            to="/docs/getting-started/installation">
            Get Started
          </Link>
        </div>
      </div>
    </header>
  );
}

function HomepageFeatures() {
  return (
    <section className={styles.features}>
      <div className="container">
        <div className="row">
          {FeatureList.map((props, idx) => (
            <Feature key={idx} {...props} />
          ))}
        </div>
      </div>
    </section>
  );
}

export default function Home(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  return (
    <Layout
      title={siteConfig.title}
      description="A cross-platform split-pane terminal built with Avalonia on the Ghostty libghostty-vt engine">
      <HomepageHeader />
      <main>
        <HomepageFeatures />
      </main>
    </Layout>
  );
}
