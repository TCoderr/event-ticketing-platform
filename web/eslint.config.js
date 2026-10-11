import js from '@eslint/js';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'node_modules', 'playwright-report', 'test-results'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  jsxA11y.flatConfigs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: { ecmaVersion: 2022, globals: { ...globals.browser } },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
      'no-restricted-globals': ['error',
        { name: 'localStorage', message: 'Oturum/secret verisi tarayıcı deposuna yazılmaz.' },
        { name: 'sessionStorage', message: 'Oturum/secret verisi tarayıcı deposuna yazılmaz.' }],
      'no-restricted-properties': ['error',
        { object: 'window', property: 'localStorage', message: 'Oturum/secret verisi tarayıcı deposuna yazılmaz.' },
        { object: 'window', property: 'sessionStorage', message: 'Oturum/secret verisi tarayıcı deposuna yazılmaz.' }],
    },
  },
  {
    files: ['e2e/**/*.{ts,mjs}', '*.config.ts', '*.config.js'],
    languageOptions: { globals: { ...globals.node } },
  },
);
