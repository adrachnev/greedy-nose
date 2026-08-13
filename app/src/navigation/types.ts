export type OnboardingStackParamList = {
  ConnectBank: undefined;
  ConnectConsent: undefined;
  Syncing: undefined;
  ClassifyDebitors: undefined;
};

export type TransactionsStackParamList = {
  TransactionList: undefined;
  TransactionDetail: { transactionId: string };
  DebitorEdit: { debtorId: string };
};

export type RulesStackParamList = {
  RulesList: undefined;
  DebitorEdit: { debtorId: string };
};

export type MainTabParamList = {
  Transactions: undefined; // nested TransactionsStackParamList
  Rules: undefined; // nested RulesStackParamList
  Settings: undefined;
};
