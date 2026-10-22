export type OnboardingStackParamList = {
  ConnectBank: undefined;
  ConnectConsent: undefined;
  Syncing: undefined;
  ClassifyPayees: undefined;
};

export type DebitsStackParamList = {
  DebitList: undefined;
  DebitDetail: { debitId: string };
  PayeeEdit: { payeeId: string };
};

export type RulesStackParamList = {
  RulesList: undefined;
  PayeeEdit: { payeeId: string };
};

export type MainTabParamList = {
  Debits: undefined; // nested DebitsStackParamList
  Rules: undefined; // nested RulesStackParamList
  Settings: undefined;
};

export type SettingsStackParamList = {
  SettingsHome: undefined;
  SelectBank: undefined;
  ConnectAttempt: undefined;
  Syncing: { bank: string; country: string };
};
