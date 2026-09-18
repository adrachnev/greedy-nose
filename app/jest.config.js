module.exports = {
  preset: '@react-native/jest-preset',
  transformIgnorePatterns: [
    // @react-native-async-storage/async-storage and @react-native-firebase/* ship ESM-only
    // (no commonjs build in their "lib"/"dist/module" output), same as the other
    // RN-ecosystem packages already listed here — they need babel-jest to run over them too.
    'node_modules/(?!(?:@react-native|react-native|@react-navigation|@react-native-async-storage|@react-native-firebase)/)',
  ],
  setupFiles: ['<rootDir>/jest.setup.js'],
};
