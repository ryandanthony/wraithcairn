import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'Wraithcairn',
  tagline: 'A cross-platform split-pane terminal',
  favicon: 'img/favicon.png',

  url: 'https://ryandanthony.github.io',
  baseUrl: '/wraithcairn/',
  organizationName: 'ryandanthony',
  projectName: 'wraithcairn',
  trailingSlash: false,

  onBrokenLinks: 'throw',

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  markdown: {
    mermaid: true,
  },

  themes: ['@docusaurus/theme-mermaid'],

  presets: [
    [
      'classic',
      {
        docs: {
          path: '../docs',
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/ryandanthony/wraithcairn/edit/main/',
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    image: 'img/wraithcairn-social-card.png',
    colorMode: {
      defaultMode: 'dark',
      disableSwitch: false,
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: 'Wraithcairn',
      logo: {
        alt: 'Wraithcairn Logo',
        src: 'img/icon.svg',
      },
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'docs',
          position: 'left',
          label: 'Docs',
        },
        {
          to: '/theme-gallery',
          label: 'Themes',
          position: 'left',
        },
        {
          href: 'https://github.com/ryandanthony/wraithcairn',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Documentation',
          items: [
            {
              label: 'Getting Started',
              to: '/docs/getting-started/installation',
            },
            {
              label: 'Guides',
              to: '/docs/guides/layouts',
            },
            {
              label: 'Reference',
              to: '/docs/reference/keyboard-shortcuts',
            },
          ],
        },
        {
          title: 'Community',
          items: [
            {
              label: 'GitHub',
              href: 'https://github.com/ryandanthony/wraithcairn',
            },
            {
              label: 'Issues',
              href: 'https://github.com/ryandanthony/wraithcairn/issues',
            },
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} Wraithcairn. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
