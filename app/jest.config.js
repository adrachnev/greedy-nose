module.exports = {
  preset: '@react-native/jest-preset',
  transformIgnorePatterns: [
    // @react-native-async-storage/async-storage ships ESM-only (no commonjs
    // build in its "lib" output), same as the other RN-ecosystem packages
    // already listed here — it needs babel-jest to run over it too.
    'node_modules/(?!(?:@react-native|react-native|@react-navigation|@react-native-async-storage)/)',
  ],
  setupFiles: ['<rootDir>/jest.setup.js'],
};
