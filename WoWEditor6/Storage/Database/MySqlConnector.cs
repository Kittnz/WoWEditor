﻿using System;
using System.Data;

namespace WoWEditor6.Storage.Database
{
    class MySqlConnector : Singleton<MySqlConnector>, IMySqlConnector
    {

        private readonly MySql.Data.MySqlClient.MySqlConnection mMySqlConn = new MySql.Data.MySqlClient.MySqlConnection();
        private bool mIsConfigured;

        public string MySqlServer { get; set; }
        public string MySqlUser { get; set; }
        public string MySqlPassword { get; set; }
        public string MySqlDatabase { get; set; }

        public MySqlConnector()
        {
            mIsConfigured = false;
        }

        public void OpenConnection()
        {
            if (string.IsNullOrEmpty(MySqlServer) || string.IsNullOrEmpty(MySqlUser) || string.IsNullOrEmpty(MySqlPassword) || string.IsNullOrEmpty(MySqlDatabase))
                throw new ArgumentException();

            if (!mIsConfigured && mMySqlConn.State != ConnectionState.Open)
                mMySqlConn.ConnectionString = string.Format("server={0};uid={1};pwd={2};database={3};", MySqlServer, MySqlUser, MySqlPassword, MySqlDatabase);

            mMySqlConn.Open();

            if (mMySqlConn.State != ConnectionState.Open)
                throw new TimeoutException("Can't connect to the server.");
        }

        public void Configuration(string pMySqlServer, string pMySqlUser, string pMySqlPassword, string pMySqlDatabase)
        {
            if (string.IsNullOrEmpty(pMySqlServer) || string.IsNullOrEmpty(pMySqlUser) || string.IsNullOrEmpty(pMySqlPassword) || string.IsNullOrEmpty(pMySqlDatabase))
                throw new ArgumentException();

            MySqlServer = pMySqlServer;
            MySqlUser = pMySqlUser;
            MySqlPassword = pMySqlPassword;
            MySqlDatabase = pMySqlDatabase;
            mIsConfigured = true;
        }

        public void CloseConnection()
        {
            if (mMySqlConn.State == ConnectionState.Open)
                mMySqlConn.Close();
        }

        public DataTable QueryToDataTable(string pQuery)
        {
            if(mMySqlConn.State == ConnectionState.Open)
            {
                DataTable retVal = new DataTable();

                MySql.Data.MySqlClient.MySqlDataAdapter mySqlDataAdapter = new MySql.Data.MySqlClient.MySqlDataAdapter(pQuery, mMySqlConn);
                mySqlDataAdapter.Fill(retVal);
                mySqlDataAdapter.Dispose();

                return retVal;
            }
            throw new TimeoutException("Can't connect to the server.");
        }
    }
}
