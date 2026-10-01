const { getDefaultConfig, mergeConfig } = require('@react-native/metro-config');

/**
 * Metro configuration
 * https://reactnative.dev/docs/metro
 *
 * @type {import('@react-native/metro-config').MetroConfig}
 */
const config = {
  transformer: {
    // Lets the guarded require of src/data/apiKey.local (gitignored, absent on
    // a fresh checkout) resolve to "missing" instead of failing the bundle.
    // Metro's default is false.
    allowOptionalDependencies: true,
  },
};

module.exports = mergeConfig(getDefaultConfig(__dirname), config);
