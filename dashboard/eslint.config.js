// Typing-escape gate for docs/developers/development-process.md "Keep contracts strict".
// tsc --strict still accepts `any`, double casts and @ts-ignore; this rejects them.
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist/**', 'node_modules/**', '*.config.ts', 'eslint.config.js'] },
  ...tseslint.configs.recommendedTypeChecked,
  {
    languageOptions: { parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname } },
    rules: {
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/ban-ts-comment': ['error', {
        'ts-ignore': true, 'ts-nocheck': true, 'ts-check': false,
        'ts-expect-error': 'allow-with-description', minimumDescriptionLength: 10,
      }],
      '@typescript-eslint/consistent-type-assertions': ['error', {
        assertionStyle: 'as', objectLiteralTypeAssertions: 'never',
      }],
      '@typescript-eslint/no-unnecessary-type-assertion': 'error',
      '@typescript-eslint/no-unsafe-argument': 'error',
      '@typescript-eslint/no-unsafe-assignment': 'error',
      '@typescript-eslint/no-unsafe-call': 'error',
      '@typescript-eslint/no-unsafe-member-access': 'error',
      '@typescript-eslint/no-unsafe-return': 'error',
      'no-restricted-syntax': ['error', {
        selector: "TSAsExpression > TSAsExpression.expression > TSUnknownKeyword.typeAnnotation",
        message: 'Double cast through unknown hides a missing contract; decode the value instead.',
      }],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
      // Not typing-contract rules: `async () => value` is the fetch-stub idiom in
      // tests, and React event attributes legitimately take async handlers.
      '@typescript-eslint/require-await': 'off',
      '@typescript-eslint/no-misused-promises': ['error', { checksVoidReturn: { attributes: false } }],
    },
  },
);
