using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.SQLite;
using System.IO;

namespace SimCityPak
{
    public class TGITable
    {
        public delegate void RegistryChangeHandler(object sender, TGIRecord e);
        public event RegistryChangeHandler RegistryChanged;

        private Dictionary<uint, TGIRecord> _cache = new Dictionary<uint, TGIRecord>();
        public Dictionary<uint, TGIRecord> Cache // SQLite cache, might not be required.
        {
            get { return _cache; }
            set { }
        }

        public List<TGIRecord> CacheSortedList
        {
            get {
                List<TGIRecord> result = Cache.Values.OrderBy(x => x.Name).ToList();
                return result;
            }
            set { }
        }

        public string _tableName; // name of SQLite table
        public string[] _tableKeys; // set table keys in override class
        public string _dbMain; // primary database supplied with a fresh copy of SCPak
        public string _dbUser; // overwrites values in Main db

        public void Close()
        {
            if (_dbMainConn != null)
                _dbMainConn.Close();

            if (_dbUserConn != null)
                _dbUserConn.Close();

            _cache.Clear();
        }

        private SQLiteConnection _dbMainConn;
        private SQLiteConnection _dbUserConn;

        private void _LoadFromDatabase(SQLiteConnection dbConn)
        {
            SQLiteCommand query = dbConn.CreateCommand();
            query.CommandText = "select * from " + _tableName;

            SQLiteDataReader reader = query.ExecuteReader();
            while (reader.Read())
            {
                TGIRecord newRecord;
                newRecord = new TGIRecord();

                uint currentId = (uint)(System.Convert.ToInt64(reader["id"]) & 0xFFFFFFFF); // masking required because SQLite uses dynamically sized int's

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string keyName = reader.GetName(i).ToString();
                    string keyValue = reader.GetValue(i).ToString();

                    if (_tableKeys.Contains(keyName)) // if we found a valid column..
                    {
                        if (newRecord.Keys.ContainsKey(keyName)) // dupe check
                        {
                            newRecord.Keys.Remove(keyName);
                        }

                        newRecord.Keys.Add(keyName, keyValue);
                    }
                }

                if (_cache.ContainsKey(currentId)) // check if we're going to override a existing value from the main DB
                {
                    _cache.Remove(currentId); // if so update the original record (could contain more info) with the user's name & comments
                }
                _cache.Add(currentId, newRecord);
            }
            reader.Dispose();
        }

        /// <summary>Resolves a database file name against the current dir, then the
        /// app's install dir, so registries load even when the working directory is
        /// not the exe folder (e.g. the CLI launched from elsewhere). Null if missing.</summary>
        private static string ResolveDbPath(string db)
        {
            if (string.IsNullOrEmpty(db)) return null;
            if (File.Exists(db)) return db;
            string exeRelative = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, db);
            return File.Exists(exeRelative) ? exeRelative : null;
        }

        public void LoadCache()
        {
            string mainDb = ResolveDbPath(_dbMain);
            if (mainDb != null)
            {
                _dbMainConn = new SQLiteConnection(String.Format("Data Source={0}", mainDb));
                _dbMainConn.Open();
                _LoadFromDatabase(_dbMainConn);
            }

            string userDb = ResolveDbPath(_dbUser);
            if (userDb != null)
            {
                _dbUserConn = new SQLiteConnection(String.Format("Data Source={0}", userDb));
                _dbUserConn.Open();
                _LoadFromDatabase(_dbUserConn);
            }
        }

        public string GetName(uint id)
        {
            TGIRecord result;
            if (_cache.TryGetValue((uint)id, out result))
            {
                return result.Name;
            }

            return id.ToHex(); // return as default value in case there is no TGIRegistry entry
        }

        public string GetAbbreviation(uint id)
        {
            if (GetName(id).Length > 3)
            {
                return GetName(id).Substring(0, 4).ToUpper();
            }

            return "UNK";
        }

        public string GetComments(uint id)
        {
            TGIRecord result;
            if (_cache.TryGetValue((uint)id, out result))
            {
                return result.Comments;
            }

            return "";
        }

        public void InsertRecord(TGIRecord newRecord)
        {
            if (_cache.ContainsKey(newRecord.Id))
            {
                _cache.Remove(newRecord.Id);
            }
            _cache.Add(newRecord.Id, newRecord);

            string queryString = "insert or replace into " + _tableName + " (" + newRecord.FormatSqlKeys() + ") values ('" + newRecord.FormatSqlValues() + "')";

            SQLiteCommand query = _dbUserConn.CreateCommand();
            query.CommandText = queryString;
            query.ExecuteNonQuery();

            if (RegistryChanged != null) // updates display names
            {
                try // nasty, triggers a non-vital thread violation sometimes when FNV importing...
                {
                    this.RegistryChanged(this, newRecord);
                }
                catch { }
            }
        }
    }
}
